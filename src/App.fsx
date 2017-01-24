#load "TicTacToe.Interpreters.fsx"
#load "Hal.fsx"
#load "ApiDocs.fsx"
#I "../packages"
#r "Suave/lib/net40/Suave.dll"

open System
open System.Net

open FSharp.Data

open Suave.Web
open Suave.Successful
open Suave.Operators
open Suave.Http
open Suave.ServerErrors
open System.IO
open Suave
open Suave.Filters
open Suave.RequestErrors

open Chessie.ErrorHandling

open Hal
open FSharpDataIntepreter

open TicTacToe
open Dsls.TicTacToeDsl
open Dsls.Free
open Instructions
open Types
open Interpreters
open Effects

let (</>) path1 path2 = Path.Combine(path1, path2)

let GAMES = "games"
let GAME = "game"
let MOVES = "moves"
let JOIN = "join"
let PLAY = "play"
let NEWGAME = "newgame"


module Paths =
    let api = "api"
    let games = api </> GAMES
    let game id = (api </> GAMES) </> id
    let moves id = game id </> MOVES
    let join id = game id </> JOIN
    let rels = sprintf "docs/rels/%s"

module Routes =
    let game = new PrintfFormat<(string -> string),unit,string,string,string>(("/" </> Paths.games) </> "%s")
    let moves = new PrintfFormat<(string -> string),unit,string,string,string>((("/" </> Paths.games) </> "%s") </> MOVES)
    let join = new PrintfFormat<(string -> string),unit,string,string,string>((("/" </> Paths.games) </> "%s") </> JOIN)
  
let gamesRel host = host </> ("games" |> Paths.rels)
let joinRel host = host </> ("join" |> Paths.rels)
let playRel host = host </> ("play" |> Paths.rels)
let newGameRel host = host </> ("newgame" |> Paths.rels)

type PlayerId = PlayerId of Guid

type Players = {
    x: PlayerId option
    o: PlayerId option
}

type PlayerMapMessage = 
    | Add     of GameId * Players
    | TryJoin of GameId * PlayerId * AsyncReplyChannel<Result<unit, Error>> 
    | TryFind of GameId * AsyncReplyChannel<Players option>

[<AutoOpen>]
module Responses =
    type Error = {
        error: string
    }
        with
        static member ToJson (x:Error) =
            JsonValue.Record [| "error", JsonValue.String(x.error) |]


[<AutoOpen>]
module Requests =
    let findString s arr = arr |> Array.find (fun (k,v) -> k = s) |> snd |> fun v -> v.ToString()

    type Play = {
        vertical: string
        horizontal: string
        playerId: string
    }
        with
        static member FromJson (x:JsonValue) =
            match x with
            | JsonValue.Record props ->
                { vertical = props |> findString "vertical"
                  horizontal = props |> findString "horizontal"
                  playerId = props |> findString "playerId" }
            | _ -> failwith "bad format"

    type Player = {
        playerId: string
    }
        with
        static member FromJson (x:JsonValue) = 
            match x with
            | JsonValue.Record props ->
                  { playerId = props |> findString "playerId" }
            | _ -> failwith "bad format"

        static member ToJson (x:Player) =
            JsonValue.Record [| "playerId", JsonValue.String(x.playerId) |]

[<AutoOpen>]
module Serialization =
    let serializeList (list: string list) : JsonValue =
        list |> List.map JsonValue.String |> List.toArray |> JsonValue.Array

    let serializeGrid (grid: string list list) : JsonValue =
        grid |> List.map serializeList |> List.toArray |> JsonValue.Array

[<AutoOpen>]
module Mappers =
    let jsonToString (json: JsonValue) = json.ToString(JsonSaveOptions.DisableFormatting)

    let toGameList url (rms: (Dsls.ReadModel.GameListItemRm * bool) list) =
        let toListItem (rm: Dsls.ReadModel.GameListItemRm, closed) = { 
            Resource.links = Map.ofList [ yield "self", [ Link.simple (url </> Paths.game rm.id) ]
                                          if not closed then yield (joinRel url, [ Link.simple (url </> Paths.join rm.id) ]) ]
            properties = Map.ofList [ "id", Hal.Pure <| JsonValue.String(rm.id)
                                      "status", Hal.Pure <| JsonValue.String(rm.status) ]
            embedded = Map.empty 
        }    
        { 
            Resource.links = Map.ofList [ "self", [ Link.simple (url </> Paths.games) ]
                                          newGameRel url, [ Link.simple (url </> Paths.games) ] ]
            properties = Map.empty
            embedded = Map.ofList [ gamesRel url, rms |> List.map toListItem ] 
        }    

    let toGameResponse url closedForJoin (rm: Dsls.ReadModel.GameRm) =
        { 
            Resource.links = Map.ofList [ yield "self", [ Link.simple (url </> Paths.game rm.id) ]
                                          yield "collection", [ Link.simple (url </> Paths.games) ]
                                          yield playRel url, [ Link.simple (url </> Paths.moves rm.id) ]
                                          if not closedForJoin then yield (joinRel url, [ Link.simple (url </> Paths.join rm.id) ])  ]
            properties = Map.ofList [ "status", Hal.Pure <| JsonValue.String(rm.status)
                                      "id", Hal.Pure <| JsonValue.String(rm.id)
                                      "grid", Hal.Pure <| serializeGrid rm.grid]
            embedded = Map.empty
        }

[<AutoOpen>]
module Deserialization =
    let getPlay (req : HttpRequest) =
        let getString rawForm = Text.Encoding.UTF8.GetString(rawForm)
        req.rawForm
        |> getString
        |> JsonValue.Parse
        |> Play.FromJson
        
let bothPlayersJoined (playerMap: Actor<PlayerMapMessage>) gameId =
    async {
        let! players = playerMap.PostAndAsyncReply(fun rc -> TryFind (gameId, rc))
        match players with
        | Some { x = Some _ ; o = Some _ } -> return true
        | _ -> return false
    }

let game interpret (playerMap: Actor<PlayerMapMessage>) (id: string) baseUrl: WebPart =
    let gameId = Guid(id) |> GameId
    fun (ctx: HttpContext) ->
        async {
            let! rm = interpret (Queries.game(gameId)) |> Async.ofAsyncResult
            let! closedForJoin = bothPlayersJoined playerMap gameId
            return! 
                match rm with
                | Ok (v,_) -> 
                    OK (v |> toGameResponse baseUrl closedForJoin |> Resource.toJson jsonInterpreter |> jsonToString) ctx
                | Bad errs -> 
                    INTERNAL_ERROR ({ error = (errs |> String.concat ", ") } |> (Error.ToJson >> jsonToString)) ctx
        }

let gamesWithJoinableFlag (interpret: Free<_> -> Effect<_>) (playerMap: Actor<PlayerMapMessage>) =
        asyncTrial {
            let! games = interpret (Queries.games) 
            let! gamesWithFlag = 
                games |> List.map (fun (g: Dsls.ReadModel.GameListItemRm) -> bothPlayersJoined playerMap (Guid(g.id) |> GameId) |> Async.map (fun b -> g,b)) 
                |> Async.Parallel
            return gamesWithFlag |> Array.toList
        }

let games interpret (playerMap: Actor<PlayerMapMessage>) baseUrl: WebPart =
    fun (ctx: HttpContext) ->
        async {
            let! rmWithJoinableFlag = gamesWithJoinableFlag interpret playerMap |> Async.ofAsyncResult
            return! 
                match rmWithJoinableFlag with
                | Ok (v,_) -> 
                    OK (v |> toGameList baseUrl |> Resource.toJson jsonInterpreter |> jsonToString) ctx
                | Bad errs ->
                    INTERNAL_ERROR ({ error = (errs |> String.concat ", ") } |> Error.ToJson |> jsonToString) ctx
        }   

let start interpret (playerMap: Actor<PlayerMapMessage>) baseUrl: WebPart =
    let gameId = Guid.NewGuid()
    let playerId = Guid.NewGuid()
    do playerMap.Post(Add(GameId gameId, { x = PlayerId playerId |> Some; o = None }))
    // handle cmd asynchronously
    do interpret (Commands.handle(GameId gameId, Start)) |> Async.ofAsyncResult|> Async.map ignore |> Async.Start
    let body = { playerId = playerId.ToString() } |> Player.ToJson |> jsonToString
    ACCEPTED body >=> Writers.setHeader "Location" (baseUrl </> Paths.game (gameId.ToString()))

let join (playerMap: Actor<PlayerMapMessage>) (gameId: string) baseUrl: WebPart =
    let playerId = Guid.NewGuid()
    fun (ctx: HttpContext) ->
        async {
            let! result = playerMap.PostAndAsyncReply(fun rc -> TryJoin (Guid(gameId) |> GameId, playerId |> PlayerId, rc))
            match result with
            | Ok _ ->
                let body = { playerId = playerId.ToString() } |> Player.ToJson |> jsonToString
                return! (ACCEPTED body >=> Writers.setHeader "Location" (baseUrl </> Paths.game (gameId.ToString()))) ctx
            | Bad errs ->
                return!
                    if errs |> List.exists ((=) "game already running") then
                        CONFLICT ({ error = (errs |> String.concat ", ") } |> Error.ToJson |> jsonToString) ctx
                    elif errs |> List.exists ((=) "game invalid") then
                        NOT_FOUND ({ error = (errs |> String.concat ", ") } |> Error.ToJson |> jsonToString) ctx
                    else 
                        INTERNAL_ERROR ({ error = (errs |> String.concat ", ")} |> Error.ToJson |> jsonToString) ctx
                        
        }

let play interpret (playerMap: Actor<PlayerMapMessage>) (id: string) (play:Play) baseUrl: WebPart =
    let toPosition (v: string, h: string) =
        match (v.ToLower(),h.ToLower()) with
        | "top", "left" -> (Top, Left) |> Some
        | "top", "hcenter" -> (Top, HCenter) |> Some
        | "top", "right" -> (Top, Right) |> Some
        | "vcenter", "left" -> (VCenter, Left) |> Some
        | "vcenter", "hcenter" -> (VCenter, HCenter) |> Some
        | "vcenter", "right" -> (VCenter, Right) |> Some
        | "bottom", "left" -> (Bottom, Left) |> Some
        | "bottom", "hcenter" -> (Bottom, HCenter) |> Some
        | "bottom", "right" -> (Bottom, Right) |> Some
        | _ -> None
    
    let maybePosition = toPosition (play.vertical, play.horizontal)
    let gameId = Guid(id) |> GameId
    fun (ctx: HttpContext) ->
        async {
            let! players = playerMap.PostAndAsyncReply(fun rc -> TryFind (gameId, rc))
            let! closedForJoin = bothPlayersJoined playerMap gameId
            match players, maybePosition, closedForJoin with
            | Some { x = Some plX; o = Some plO }, Some(v,h), true ->
                let cmd = if (Guid(play.playerId) |> PlayerId) = plX then PlayX else PlayO
                // handle cmd asyncronously
                do interpret (Commands.handle(gameId, cmd (v, h))) |> Async.ofAsyncResult|> Async.map ignore |> Async.Start
                return! (ACCEPTED "{}" >=> Writers.setHeader "Location" (baseUrl </> Paths.game (gameId.ToString()))) ctx
            | Some _, Some _, false -> return! CONFLICT ({ error = "opponent hasn't joined game yet" } |> Error.ToJson |> jsonToString) ctx
            | _, None, _            -> return! BAD_REQUEST ({ error = "unknown position" } |> Error.ToJson |> jsonToString) ctx
            | _                     -> return! NOT_FOUND ({ error = "unknown player id" } |> Error.ToJson |> jsonToString) ctx
        }

let playersMapActor = 
    Actor.Start(fun inbox ->
        let rec loop (playersMap: Map<GameId, Players>) =
            async {
                let! msg = inbox.Receive()
                match msg with
                | Add (gameId, players) ->
                    return! loop (playersMap.Add(gameId, players))
                | TryJoin (gameId, player, rc) -> 
                    match playersMap.TryFind gameId with
                    | Some { x = Some _; o = Some _ } ->
                        rc.Reply(fail "game already running")
                        return! loop playersMap
                    | Some { x = Some pX; o = None } ->
                        rc.Reply(ok ())
                        return! loop (playersMap.Add(gameId, { x = Some pX; o = player |> Some }))
                    | _ -> 
                        rc.Reply(fail "game invalid")
                        return! loop playersMap
                | TryFind (gameId, rc) ->
                    rc.Reply(playersMap.TryFind gameId)
                    return! loop playersMap
            }
        loop Map.empty)

let interpret free =
    TicTacToe.interpret
        Domain.interpret
        EventBus.interpret
        EventStore.interpret
        ReadModel.interpret free

let app =
    let urlWithHost (request : HttpRequest) = 
        let host = 
            request.headers
            |> List.find (fst >> (=) "host")
            |> snd
        sprintf "%s://%s" request.url.Scheme host
    
    let setJsonHeader = Writers.setMimeType "application/hal+json"

    let setCorsHaeders = 
        Writers.setHeader "Access-Control-Allow-Origin" "*" 
        >=> Writers.setHeader "Access-Control-Allow-Headers" "content-type" 
        >=> Writers.setHeader "Access-Control-Allow-Methods" "POST, GET, OPTIONS, DELETE, PATCH"

    let setHeaders = setJsonHeader >=> setCorsHaeders

    choose [ 
        GET >=> choose [
            path ("/" </> Paths.api) >=> request (urlWithHost >> fun host -> 
                { Resource.empty with links = Map.ofList [ "self", [ Link.simple (host </> Paths.api) ]
                                                           gamesRel host, [ Link.simple (host </> Paths.games) ] ] }
                |> Resource.toJson jsonInterpreter |> jsonToString |> OK) 
                >=> setHeaders
            path ("/" </> Paths.games) >=> request (urlWithHost >> games interpret playersMapActor) >=> setHeaders
            pathScan Routes.game (fun gameId -> 
                request (urlWithHost >> game interpret playersMapActor gameId)) >=> setHeaders
            path ("/" </> Paths.rels GAMES) >=> Files.file "./public/rels/games.html" >=> setCorsHaeders
            path ("/" </> Paths.rels NEWGAME) >=> Files.file "./public/rels/newgame.html" >=> setCorsHaeders
            path ("/" </> Paths.rels JOIN) >=> Files.file "./public/rels/join.html" >=> setCorsHaeders            
            path ("/" </> Paths.rels PLAY) >=> Files.file "./public/rels/play.html" >=> setCorsHaeders
        ]
        POST >=> choose [
            path ("/" </> Paths.games) >=> request (urlWithHost >> start interpret playersMapActor) >=> setHeaders
            pathScan Routes.join (fun gameId ->
                request (urlWithHost >> join playersMapActor gameId)) >=> setHeaders
            pathScan Routes.moves (fun gameId -> 
                request (fun req -> play interpret playersMapActor gameId (getPlay req) (urlWithHost req)))
                >=> setHeaders
        ]
    ]

let config =
    let ip = IPAddress.Parse "0.0.0.0"
    let [|_; port|] = fsi.CommandLineArgs
    { defaultConfig with
        //logger = Logging.LoggerEx   .saneDefaultsFor Logging.LogLevel.Info
        bindings= [ HttpBinding.create HTTP ip (uint16 port) ] }

interpret (ReadModel.subscribe())

startWebServer config app
