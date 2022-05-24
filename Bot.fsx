// Initialize 1
let env = System.Environment.GetEnvironmentVariable
let token = env "TOKEN" // https://discordhelp.net/discord-token
let commandGuildId = uint64 (env "COMMAND_GUILD_ID")
let commandChannelId = uint64 (env "COMMAND_CHANNEL_ID") // A channel in commandGuildId
let logChannelId = uint64 (env "LOG_CHANNEL_ID") // A channel in commandGuildId
let targetFreshmenGuildId = uint64 (env "TARGET_FRESHMEN_GUILD_ID")
let logWebhook = env "LOG_WEBHOOK"

#r "nuget: System.Threading.RateLimiting, 7.0.0-preview.4.22229.4"
#r "nuget: Leaf.xNet, 5.2.10"
#r "nuget: Newtonsoft.Json, 13.0.1"
#r "nuget: System.Drawing.Common, 7.0.0-preview.2.22152.2"
#r "nuget: websocketsharp.core, 1.0.0"
#r "Anarchy.dll" // Custom built from https://github.com/not-ilinked/Anarchy/pull/3302
open Discord
open Discord.Gateway
open System.Threading.RateLimiting

(task {
    let completion = System.Threading.Tasks.TaskCompletionSource()
    let mutable finishedInit = false
    // Initialize 2
    let loggedInMsg = "Logged in"
    let http = new System.Net.Http.HttpClient()
    let send obj = // https://birdie0.github.io/discord-webhooks-guide
        task {
            let json = System.Net.Http.Json.JsonContent.Create obj
            let! _ = http.PostAsync(logWebhook, json)
            ()
        }
    let log, error =
        let log (color: int) (message: string) =
            printfn $"{message}"
            send {|
                embeds = [
                    {|
                        color = color
                        description = message
                    |}
                ]
            |}
        log 1127128, log 16711680
    try
        // Initialize 3
        let client = new Gateway.DiscordSocketClient()
        client.Login token
        let emojisRateLimiter = new FixedWindowRateLimiter(FixedWindowRateLimiterOptions(1, QueueProcessingOrder.NewestFirst, 0, System.TimeSpan.FromSeconds 15.))
        let reactRateLimiter = new FixedWindowRateLimiter(FixedWindowRateLimiterOptions(1, QueueProcessingOrder.NewestFirst, 20, System.TimeSpan.FromSeconds 10.))
        let messageRateLimiter = new FixedWindowRateLimiter(FixedWindowRateLimiterOptions(1, QueueProcessingOrder.NewestFirst, 0, System.TimeSpan.FromSeconds 10.))
        let reactCheckRateLimiter = new FixedWindowRateLimiter(FixedWindowRateLimiterOptions(1, QueueProcessingOrder.NewestFirst, 10, System.TimeSpan.FromSeconds 2.))
        let mutable emojis = Array.empty :> System.Collections.Generic.IReadOnlyList<DiscordEmoji>
        let mutable messageReceivedHandler = fun _ _ -> ()
        let mutable reactionAddedHandler = fun _ _ -> ()
        client.add_OnMessageReceived (fun client args -> messageReceivedHandler client args)
        client.add_OnMessageReactionAdded (fun client args -> reactionAddedHandler client args)

        // Main
        messageReceivedHandler <- fun client args ->
            task {
                try
                    if args.Message.Guild <> null then
                        if args.Message.Guild.Id = targetFreshmenGuildId then
                            if args.Message.Content |> isNull |> not then
                                let content = args.Message.Content.ToLower()
                                let contains (s: string) = content.Contains s
                                let split = content.Split()
                                let containsOnItsOwn (s: string) = Array.contains s split
                                let tryReact name fallbackImageUrl = task {
                                    use! lease = emojisRateLimiter.WaitAsync 1
                                    if lease.IsAcquired then
                                        let! newEmojis = client.GetGuildEmojisAsync targetFreshmenGuildId
                                        emojis <- newEmojis
                                    match emojis |> Seq.tryFind (fun e -> e.Name = name) with
                                    | Some emoji ->
                                        use! lease = reactRateLimiter.WaitAsync 1
                                        if lease.IsAcquired then
                                            do! args.Message.AddReactionAsync(name, emoji.Id)
                                    | None ->
                                        use! lease = messageRateLimiter.WaitAsync 1
                                        if lease.IsAcquired then
                                            let! _ = client.SendMessageAsync(args.Message.Channel.Id, MessageProperties(Content = fallbackImageUrl))
                                            ()
                                }
                                if contains "mark" || contains "cag" || contains "cag2" then
                                    do! tryReact "mark" "https://cdn.discordapp.com/emojis/978324790337228800.webp?size=20&quality=lossless"
                                if containsOnItsOwn ":>" then
                                    do! tryReact "wallace" "https://cdn.discordapp.com/emojis/960527234278510632.webp?size=20&quality=lossless"
                                let oCount = Seq.filter ((=) 'o') content |> Seq.length
                                if containsOnItsOwn ":o" || float oCount / float content.Length > 0.8 then
                                    do! tryReact "wallaceO" "https://cdn.discordapp.com/emojis/971743699753140244.webp?size=20&quality=lossless"
                                if containsOnItsOwn ":<" then
                                    do! tryReact "wallacent" "https://cdn.discordapp.com/emojis/961550530965045298.webp?size=20&quality=lossless"
                                if containsOnItsOwn "mok" || containsOnItsOwn "perry" then
                                    do! tryReact "oxford" "https://cdn.discordapp.com/emojis/898106541117411378.webp?size=20&quality=lossless"
                                if containsOnItsOwn "100" then
                                    use! lease = reactRateLimiter.WaitAsync 1
                                    if lease.IsAcquired then
                                        do! args.Message.AddReactionAsync "🤖"
                        elif args.Message.Guild.Id = commandGuildId then
                            let stop() = task {
                                do! args.Message.AddReactionAsync "✅"
                                client.Logout()
                                http.Dispose()
                                client.Dispose()
                                emojisRateLimiter.Dispose()
                                reactRateLimiter.Dispose()
                                messageRateLimiter.Dispose()
                                exit 0
                            }
                            if args.Message.Channel.Id = logChannelId then
                                if args.Message.Embed <> null && args.Message.Embed.Description = loggedInMsg then
                                    if finishedInit then do! stop() else finishedInit <- true
                            elif args.Message.Channel.Id = commandChannelId then
                                match args.Message.Content with
                                | "stop" -> do! stop()
                                | _ -> do! args.Message.AddReactionAsync "❓"
                with e -> do! error (string e)
            } |> ignore
        reactionAddedHandler <- fun client args ->
            task {
                try
                    if args.Reaction.Guild <> null && args.Reaction.Guild.Id = targetFreshmenGuildId && args.Reaction.UserId <> client.User.Id then // HKUST Freshmen server
                        use! lease = reactCheckRateLimiter.WaitAsync 1
                        if lease.IsAcquired then
                            let! reacts = client.GetMessageReactionsAsync(args.Reaction.Channel.Id, args.Reaction.MessageId, ReactionQuery(ReactionId = args.Reaction.Emoji.Id, ReactionName = args.Reaction.Emoji.Name))
                            if reacts |> Seq.exists (fun reacter -> reacter.Id = client.User.Id) |> not then
                                use! lease = reactRateLimiter.WaitAsync 1
                                if lease.IsAcquired then
                                    do! client.AddMessageReactionAsync(args.Reaction.Channel.Id, args.Reaction.MessageId, args.Reaction.Emoji.Name, args.Reaction.Emoji.Id)
                with e -> do! error (string e)
            } |> ignore
        // Only for GitHub Actions: Wait indefinitely until stop
        do! log loggedInMsg
        do! System.Threading.Tasks.Task.Delay (System.TimeSpan.FromSeconds 20.) // Ignore any logged in messages generated by this instance
        if not finishedInit then finishedInit <- true
        do! completion.Task
    with e -> do! error (string e)
}).Wait()
