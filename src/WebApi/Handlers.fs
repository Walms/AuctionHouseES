﻿module AuctionHouseES.Handlers

open System
open Giraffe
open Marten
open Microsoft.AspNetCore.Http
open Events

type CreateAuctionRequest = 
    {
        AuctionId: AuctionId
        StartedBy: UserId
        StartsOn: DateTimeOffset
        EndsOn: DateTimeOffset
        Title: string
        Description: string
        MinimumBid: decimal option
    }

let createAuction (req: CreateAuctionRequest) (next: HttpFunc) (ctx: HttpContext) =
    task {
        let auctionCreated : Events.AuctionCreated = 
            {
                Id = req.AuctionId
                Title = req.Title
                Description = req.Description
                StartedBy = req.StartedBy
                StartsOn = req.StartsOn
                EndsOn = req.EndsOn
                MinimumBid = req.MinimumBid
            }

        let store = ctx.GetService<IDocumentStore>()
        use session = store.LightweightSession()        
        session.Events.StartStream(req.AuctionId, [ box auctionCreated ]) |> ignore
        do! session.SaveChangesAsync()
        return! Successful.OK() next ctx        
    }

type CancelAuctionRequest = { AuctionId: AuctionId; CanceledBy: UserId; Reason: string }

let cancelAuction (req: CancelAuctionRequest) (next: HttpFunc) (ctx: HttpContext) =
    task {
        let auctionCanceled : Events.AuctionCanceled = 
            {
                Id = req.AuctionId
                CanceledBy = req.CanceledBy
                CanceledOn = DateTimeOffset.Now
                Reason = req.Reason
            }

        let store = ctx.GetService<IDocumentStore>()
        use session = store.LightweightSession()

        // Validate against the current state
        let! aggregate = session.Events.AggregateStreamAsync<Projections.Auction>(req.AuctionId)
        match aggregate.Status with 
        | Projections.AuctionStatus.Canceled -> 
            return! RequestErrors.conflict(text "Auction has already been canceled.") next ctx
        | Projections.AuctionStatus.Ended -> 
            return! RequestErrors.conflict(text "Auction has already ended.") next ctx
        | Projections.AuctionStatus.Created
        | Projections.AuctionStatus.Started -> 
            session.Events.Append(req.AuctionId, [ box auctionCanceled ]) |> ignore
            do! session.SaveChangesAsync()
            return! Successful.OK() next ctx
    }

    
type BidRequest = { AuctionId: AuctionId; Bidder: UserId; Amount: decimal }

let placeBid (req: BidRequest) (next: HttpFunc) (ctx: HttpContext) =
    task {
        let bidPlaced : Events.BidPlaced = 
            {
                Events.BidPlaced.Bidder = req.Bidder
                Events.BidPlaced.Amount = req.Amount
                Events.BidPlaced.ReceivedOn = DateTimeOffset.Now
            }

        let store = ctx.GetService<IDocumentStore>()
        use session = store.LightweightSession()

        // Validate against the current state
        let! aggregate = session.Events.AggregateStreamAsync<Projections.Auction>(req.AuctionId)
        match aggregate.Status with 
        | Projections.AuctionStatus.Created -> 
            return! RequestErrors.conflict(text "Auction has not started yet.") next ctx
        | Projections.AuctionStatus.Canceled -> 
            return! RequestErrors.conflict(text "Auction has already been canceled.") next ctx
        | Projections.AuctionStatus.Ended -> 
            return! RequestErrors.conflict(text "Auction has already ended.") next ctx
        | Projections.AuctionStatus.Started -> 
            session.Events.Append(req.AuctionId, [ box bidPlaced ]) |> ignore
            do! session.SaveChangesAsync()
            return! Successful.OK() next ctx
    }

let getAuction (auctionId: AuctionId) (next: HttpFunc) (ctx: HttpContext) = 
    task {
        let store = ctx.GetService<IDocumentStore>()
        use session = store.LightweightSession()

        // If using "Live" aggregation
        let! aggregate = session.Events.AggregateStreamAsync<Projections.Auction>(auctionId)
        return! Successful.ok (json aggregate) next ctx
    }