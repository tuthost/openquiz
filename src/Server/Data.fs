module rec Data

open System
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.DocumentModel
open Microsoft.FSharp.Reflection

open Domain
open Common

let v2 = DynamoDBEntryConversion.V2

let client = new AmazonDynamoDBClient()

let loadTable name =
    Table.LoadTable(client, (sprintf "OQ-%s" name))

let readAll search =
    seq{
        let rec readPage (search : Search) =
            seq{
                yield! search.GetNextSetAsync().Result

                if not search.IsDone then
                    yield! readPage search
            }

        yield! readPage search
    }

//#region Common Converters

let toString (x:'a) =
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

let fromString<'a> (s:string) =
    match FSharpType.GetUnionCases typeof<'a> |> Array.filter (fun case -> case.Name = s) with
    |[|case|] -> Some(FSharpValue.MakeUnion(case,[||]) :?> 'a)
    |_ -> None

let stringOfDoc (doc : Document) attr =
    match doc.TryGetValue attr with
    | true, en -> en.AsString()
    | _ -> ""

let boolOfDoc (doc : Document) attr =
    match doc.TryGetValue attr with
    | true, en -> en.AsBoolean()
    | _ -> false

let intOfDoc (doc : Document) attr =
    match doc.TryGetValue attr with
    | true, en -> en.AsInt()
    | _ -> 0

let listOfDoc (doc : Document) attr  =
    match doc.TryGetValue attr with
    | true, en -> en.AsListOfPrimitive()
    | _ -> new Collections.Generic.List<Primitive>()

let optionOfEntry (doc : Document) attr  =
    match doc.TryGetValue attr with
    | true, entry when (entry :? DynamoDBNull) -> None
    | true, entry  -> Some (v2.ConvertFromEntry entry)
    | _ -> None

let entryOfOption = function
    | None -> DynamoDBNull() :> DynamoDBEntry
    | Some x -> v2.ConvertToEntry x

//#endregion

module RefreshTokens =
    let add token =
        let table = loadTable "RefreshTokens"
        let refreshTokenItem = Document()
        refreshTokenItem.["Token"] <- v2.ConvertToEntry  token.Value
        refreshTokenItem.["Expired"] <- v2.ConvertToEntry token.Expired
        (table.PutItemAsync (refreshTokenItem)).Wait()

    let get (tokenValue : string) =
        let table = loadTable "RefreshTokens"
        let task = table.GetItemAsync(Primitive(tokenValue))
        match task.Result with
        | null -> None
        | doc -> Some {Value = doc.["Token"].AsString(); Expired = doc.["Expired"].AsDateTime()}

    let replace oldToken newToken =
        let table = loadTable "RefreshTokens"
        table.DeleteItemAsync(Primitive(oldToken.Value)) |> ignore
        add newToken

module Experts =
    let private expertOfDocument (doc:Document) : Expert option =
        let id = doc.["Id"].AsString()
        let username = stringOfDoc doc "Username"
        let isProducer = boolOfDoc doc "IsProducer"
        let competitions =
            doc.["Competitions"].AsDocument()
            |> Seq.map (fun pair -> Int32.Parse(pair.Key), pair.Value.AsInt())
            |> Map.ofSeq

        let quizes =
            listOfDoc doc "Quizzes"
            |> Seq.map (fun p -> p.AsInt())
            |> List.ofSeq

        let packages =
            listOfDoc doc "Packages"
            |> Seq.map (fun p -> p.AsInt())
            |> List.ofSeq

        let version = intOfDoc doc "Version"

        Some {Id = id; Username = username; IsProducer = isProducer; Competitions = competitions;
            Quizes = quizes; Packages = packages; Version = version}

    let private documentOfExpert (exp:Expert) =
        let expItem = Document()

        expItem.["Id"] <- v2.ConvertToEntry  (exp.Id.ToString())
        expItem.["Username"] <- v2.ConvertToEntry exp.Username
        expItem.["IsProducer"] <- v2.ConvertToEntry exp.IsProducer

        let regsEntry = Document()
        for quizId, competitorId in exp.Competitions |> Seq.map (fun pair -> pair.Key,pair.Value) do
             regsEntry.[quizId.ToString()] <- v2.ConvertToEntry competitorId

        expItem.["Competitions"] <- regsEntry

        let quizzesEntry = PrimitiveList()
        for quizId in exp.Quizes do
            quizzesEntry.Add(Primitive.op_Implicit quizId)
        expItem.["Quizzes"] <- quizzesEntry

        let packagesEntry = PrimitiveList()
        for packageId in exp.Packages do
            packagesEntry.Add(Primitive.op_Implicit packageId)
        expItem.["Packages"] <- packagesEntry

        expItem.["Version"] <- v2.ConvertToEntry exp.Version

        expItem

    let get (id:string) =
        async{
            let table = loadTable "Experts"
            let! docOrNull = table.GetItemAsync(Primitive.op_Implicit (id.ToString())) |> Async.AwaitTask
            return
                match docOrNull with
                | null -> None
                | doc -> expertOfDocument doc

        } |> Async.RunSynchronously

    let update (exp:Expert) =
        async{

            let exp = {exp with Version = exp.Version + 1}

            let doc = documentOfExpert exp
            let table = loadTable "Experts"
            let! _ = table.UpdateItemAsync (doc) |> Async.AwaitTask

            return exp

        } |> Async.RunSynchronously

module Quizzes =

    let getMaxId() =
        let quizzes = getDescriptors()
        if List.isEmpty quizzes then 0
        else (List.maxBy (fun (quiz : QuizDescriptor) -> quiz.QuizId) quizzes).QuizId

    let update (quiz:Quiz) =
        let quiz = {quiz with Version = quiz.Version + 1}

        let quizItem = documentOfQuiz quiz
        let table = loadTable "Quizzes"
        (table.UpdateItemAsync (quizItem)).Wait()
        quiz

    let getDescriptors () =

        let table = loadTable "Quizzes"
        let config = ScanOperationConfig()

        config.AttributesToGet <- new Collections.Generic.List<string> ([
             "Id"; "Producer"; "StartTime"; "Brand"; "Name"; "Status"; "WelcomeText"; "FarewellText";
                    "IsPrivate"; "ImgKey";  "WithPremoderation"; "AdminToken"; "RegToken"; "ListenToken";
                    "PkgId"; "PkgQwIdx"; "EventPage"])
        config.Select <- SelectValues.SpecificAttributes

        table.Scan(config)
        |> readAll
        |> Seq.map descriptorOfDocument
        |> List.ofSeq

    let getDescriptor (quizId:int) : QuizDescriptor option =
        let config = GetItemOperationConfig()
        config.AttributesToGet <- new Collections.Generic.List<string> ([
             "Id"; "Producer"; "StartTime"; "Brand"; "Name"; "Status"; "WelcomeText"; "FarewellText";
                    "IsPrivate"; "ImgKey";  "WithPremoderation"; "AdminToken"; "RegToken"; "ListenToken";
                    "PkgId"; "PkgQwIdx"; "EventPage"])

        let table = loadTable "Quizzes"
        let task = table.GetItemAsync((Primitive.op_Implicit quizId), config)

        match task.Result with
        | null -> None
        | doc -> Some <| descriptorOfDocument doc

    let get (quizId : int) : Quiz option=
        let table = loadTable "Quizzes"

        let task = table.GetItemAsync(Primitive.op_Implicit quizId)

        match task.Result with
        | null -> None
        | doc -> Some <| quizOfDocument doc

//#region Converters

    let documentOfQuiz (quiz:Quiz) =
        let gameItem = Document()
        gameItem.["Id"] <- v2.ConvertToEntry  quiz.Dsc.QuizId
        gameItem.["Name"] <- v2.ConvertToEntry quiz.Dsc.Name
        gameItem.["Producer"] <- v2.ConvertToEntry quiz.Dsc.Producer
        gameItem.["Brand"] <- v2.ConvertToEntry quiz.Dsc.Brand
        gameItem.["ImgKey"] <- v2.ConvertToEntry quiz.Dsc.ImgKey
        gameItem.["StartTime"] <- entryOfOption quiz.Dsc.StartTime
        gameItem.["Status"] <- v2.ConvertToEntry <| quiz.Dsc.Status.ToString()
        gameItem.["WelcomeText"] <- v2.ConvertToEntry quiz.Dsc.WelcomeText
        gameItem.["FarewellText"] <- v2.ConvertToEntry quiz.Dsc.FarewellText
        gameItem.["IsPrivate"] <- v2.ConvertToEntry quiz.Dsc.IsPrivate
        gameItem.["WithPremoderation"] <- v2.ConvertToEntry quiz.Dsc.WithPremoderation
        gameItem.["ListenToken"] <- v2.ConvertToEntry quiz.Dsc.ListenToken
        gameItem.["AdminToken"] <- v2.ConvertToEntry quiz.Dsc.AdminToken
        gameItem.["RegToken"] <- v2.ConvertToEntry quiz.Dsc.RegToken
        gameItem.["PkgId"] <- entryOfOption quiz.Dsc.PkgId
        gameItem.["PkgQwIdx"] <- entryOfOption quiz.Dsc.PkgQwIdx
        gameItem.["EventPage"] <- v2.ConvertToEntry quiz.Dsc.EventPage

        let questionsEntry = DynamoDBList()
        for qw in quiz.Questions do
             let qwItem = Document()
             qwItem.["Name"] <- v2.ConvertToEntry qw.Name
             qwItem.["Seconds"] <- v2.ConvertToEntry qw.Seconds
             qwItem.["Status"] <- v2.ConvertToEntry <| qw.Status.ToString()
             qwItem.["Text"] <- v2.ConvertToEntry qw.Text
             qwItem.["ImgKey"] <- v2.ConvertToEntry qw.ImgKey
             qwItem.["Answer"] <- v2.ConvertToEntry qw.Answer
             qwItem.["Comment"] <- v2.ConvertToEntry qw.Comment
             qwItem.["CommentImgKey"] <- v2.ConvertToEntry qw.CommentImgKey
             qwItem.["StartTime"] <- entryOfOption qw.StartTime
             questionsEntry.Add(qwItem)

        gameItem.["Questions"] <- questionsEntry
        gameItem.["Version"] <- v2.ConvertToEntry quiz.Version

        gameItem

    let private descriptorOfDocument (doc:Document) : QuizDescriptor =
        {
            QuizId = doc.["Id"].AsInt()
            Producer = stringOfDoc doc "Producer"
            StartTime = optionOfEntry doc "StartTime"
            Brand = stringOfDoc doc "Brand"
            Name = stringOfDoc doc "Name"
            Status = defaultArg (fromString (doc.["Status"].AsString())) Draft
            WelcomeText = stringOfDoc doc "WelcomeText"
            FarewellText = stringOfDoc doc "FarewellText"
            IsPrivate = boolOfDoc doc "IsPrivate"
            ImgKey = stringOfDoc doc "ImgKey"
            AdminToken = stringOfDoc doc "AdminToken"
            ListenToken = stringOfDoc doc "ListenToken"
            RegToken = stringOfDoc doc "RegToken"
            WithPremoderation = boolOfDoc doc "WithPremoderation"
            PkgId = optionOfEntry doc "PkgId"
            PkgQwIdx = optionOfEntry doc "PkgQwIdx"
            EventPage = stringOfDoc doc "EventPage"
        }

    let quizOfDocument (doc:Document) : Quiz =
        let dsc = descriptorOfDocument doc

        let version = doc.["Version"].AsInt()

        let qwList = doc.["Questions"].AsListOfDocument()
        let questions =
            qwList
            |> Seq.map quizQuestionOfDocument
            |> List.ofSeq

        {Dsc = dsc; Version = version; Questions = questions}

    let quizQuestionOfDocument  (qwDoc:Document) =
        let qwName = stringOfDoc qwDoc "Name"
        let qwSeconds = qwDoc.["Seconds"].AsInt()
        let qwStatus = defaultArg (fromString (stringOfDoc qwDoc "Status")) Announcing
        let qwStartTime = optionOfEntry qwDoc "StartTime"
        let qwText = stringOfDoc qwDoc "Text"
        let qwImgKey = stringOfDoc qwDoc "ImgKey"
        let qwAnswer = stringOfDoc qwDoc "Answer"
        let qwComment = stringOfDoc qwDoc "Comment"
        let qwCommentImgKey = stringOfDoc qwDoc "CommentImgKey"

        {Name = qwName; Seconds = qwSeconds; Status = qwStatus; Text = qwText; ImgKey = qwImgKey; Answer = qwAnswer; Comment = qwComment
         CommentImgKey = qwCommentImgKey; StartTime = qwStartTime}

//#endregion

module Teams =
    let getMaxId quizId =
        let descriptors = getDescriptors quizId
        if descriptors.Length > 0 then (descriptors |> List.maxBy (fun t -> t.TeamId)).TeamId else 0

    let update (team : Team) =
        let team = {team with Version = team.Version + 1}

        let teamItem = documentOfTeam team
        let table = loadTable "Teams"
        (table.UpdateItemAsync (teamItem)).Wait()
        team

    let getIds (quizId: int) : int list=
        let table = loadTable "Teams"

        let keyExpression = Expression()
        keyExpression.ExpressionAttributeNames.["#N"] <- "QuizId"
        keyExpression.ExpressionAttributeValues.[":v"] <- Primitive.op_Implicit quizId
        keyExpression.ExpressionStatement <- "#N = :v"

        let config = QueryOperationConfig()
        config.KeyExpression <- keyExpression
        config.AttributesToGet <- new Collections.Generic.List<string> (["TeamId"] )
        config.Filter <- QueryFilter()
        config.Select <- SelectValues.SpecificAttributes

        table.Query(config)
        |> readAll
        |> Seq.map (fun (doc:Document) -> doc.["TeamId"].AsInt())
        |> List.ofSeq

    let getDescriptors (quizId: int) : TeamDescriptor list=
        let table = loadTable "Teams"

        let keyExpression = Expression()
        keyExpression.ExpressionAttributeNames.["#N"] <- "QuizId"
        keyExpression.ExpressionAttributeValues.[":v"] <- Primitive.op_Implicit quizId
        keyExpression.ExpressionStatement <- "#N = :v"

        let config = QueryOperationConfig()
        config.KeyExpression <- keyExpression
        config.AttributesToGet <- new Collections.Generic.List<string> ([ "QuizId"; "TeamId"; "Name"; "Status"; "EntryToken"; "RegistrationDate"; "ActiveSessionId"] )
        config.Filter <- QueryFilter()
        config.Select <- SelectValues.SpecificAttributes

        table.Query(config)
        |> readAll
        |> Seq.map descriptorOfDocument
        |> List.ofSeq

    let getDescriptor (quizId: int) (teamId: int) : TeamDescriptor option =
        let config = GetItemOperationConfig()
        config.AttributesToGet <- new Collections.Generic.List<string> ([ "QuizId"; "TeamId"; "Name"; "Status"; "EntryToken"; "RegistrationDate"; "ActiveSessionId"] )

        let table = loadTable "Teams"
        let task = table.GetItemAsync(Primitive.op_Implicit quizId, Primitive.op_Implicit teamId, config)

        match task.Result with
        | null -> None
        | doc -> Some (descriptorOfDocument doc)

    let get (quizId:int) (teamId:int) : Team option=
        let table = loadTable "Teams"
        let task = table.GetItemAsync(Primitive.op_Implicit quizId, Primitive.op_Implicit teamId)

        match task.Result with
        | null -> None
        | doc -> Some (teamOfDocument doc)

    let getAllInQuiz (quizId:int) : Team list =
        let table = loadTable "Teams"

        table.Query(Primitive.op_Implicit quizId, QueryFilter())
        |> readAll
        |> Seq.map teamOfDocument
        |> List.ofSeq

//#region Converters

    let private documentOfTeam (team:Team) =
        let teamItem = Document()
        teamItem.["QuizId"] <- v2.ConvertToEntry  team.Dsc.QuizId
        teamItem.["TeamId"] <- v2.ConvertToEntry  team.Dsc.TeamId

        teamItem.["Name"] <- v2.ConvertToEntry team.Dsc.Name
        teamItem.["Status"] <- v2.ConvertToEntry <| team.Dsc.Status.ToString()
        teamItem.["RegistrationDate"] <- v2.ConvertToEntry <| team.Dsc.RegistrationDate
        teamItem.["EntryToken"] <- v2.ConvertToEntry <| team.Dsc.EntryToken
        teamItem.["ActiveSessionId"] <- v2.ConvertToEntry <| team.Dsc.ActiveSessionId

        let answersEntry = Document()
        for index,aw in team.Answers |> Seq.map (fun pair -> pair.Key,pair.Value) do
             let awItem = Document()
             awItem.["Text"] <- v2.ConvertToEntry aw.Text
             awItem.["RecieveTime"] <- v2.ConvertToEntry aw.RecieveTime
             awItem.["Result"] <- entryOfOption aw.Result
             awItem.["IsAutoResult"] <- v2.ConvertToEntry aw.IsAutoResult
             awItem.["UpdateTime"] <- entryOfOption aw.UpdateTime
             answersEntry.[index.ToString()] <- awItem

        teamItem.["Answers"] <- answersEntry
        teamItem.["Version"] <- v2.ConvertToEntry team.Version

        teamItem

    let private teamOfDocument (doc:Document) : Team=
        let dsc = descriptorOfDocument doc
        let version = doc.["Version"].AsInt()

        let awMap = doc.["Answers"].AsDocument()
        let answers =
            awMap
            |> Seq.map (fun pair -> Int32.Parse(pair.Key), teamAnswerOfDocument(pair.Value))
            |> Map.ofSeq

        {Dsc = dsc; Version = version; Answers = answers}

    let private descriptorOfDocument (doc:Document) : TeamDescriptor =
        {
            QuizId = doc.["QuizId"].AsInt()
            TeamId = doc.["TeamId"].AsInt()
            Name = stringOfDoc doc "Name"
            Status = defaultArg (fromString (doc.["Status"].AsString())) New
            EntryToken = stringOfDoc doc "EntryToken"
            RegistrationDate = doc.["RegistrationDate"].AsDateTime()
            ActiveSessionId = doc.["ActiveSessionId"].AsInt()
        }

    let private teamAnswerOfDocument  (entry:DynamoDBEntry) =
        let awDoc = entry.AsDocument()

        let stringOfEntry (doc : Document) attr =
            match doc.TryGetValue attr with
            | true, en -> en.AsString()
            | _ -> ""

        let awText = if awDoc.ContainsKey "Text" then awDoc.["Text"].AsString() else ""
        let awRecieveTime = awDoc.["RecieveTime"].AsDateTime()
        let awResult = optionOfEntry awDoc "Result"
        let awUpdateTime = optionOfEntry awDoc "UpdateTime"
        let awIsAutoResult = boolOfDoc awDoc "IsAutoResult"

        {Text = awText; RecieveTime = awRecieveTime; Result = awResult; IsAutoResult = awIsAutoResult; UpdateTime = awUpdateTime}

//#endregion

module Packages =

    let getMaxId quizId =
        let descriptors = getDescriptors quizId
        if descriptors.Length > 0 then (descriptors |> List.maxBy (fun t -> t.PackageId)).PackageId else 0

    let update (pkg : Package) =
        printfn "%A" pkg
        let pkg =  {pkg with Version = pkg.Version + 1}

        printfn "%A" pkg
        let teamItem = documentOfPackage pkg
        let table = loadTable "Packages"
        (table.UpdateItemAsync (teamItem)).Wait()
        pkg

    let getDescriptors () : PackageDescriptor list =
        let config = ScanOperationConfig()
        config.Select <- SelectValues.SpecificAttributes
        config.AttributesToGet <- new Collections.Generic.List<string> ([ "Id"; "Producer"; "Name"] )

        let table = loadTable "Packages"

        table.Scan(config)
        |> readAll
        |> Seq.map descriptorOfDocument
        |> List.ofSeq


    let getDescriptor (packageId: int) : PackageDescriptor option =
        let config = GetItemOperationConfig()
        config.AttributesToGet <- new Collections.Generic.List<string> ([ "Id"; "Producer"; "Name"] )

        let table = loadTable "Packages"
        let task = table.GetItemAsync(Primitive.op_Implicit packageId, config)

        match task.Result with
        | null -> None
        | doc -> Some (descriptorOfDocument doc)

    let get  (packageId: int) : Package option =
        let table = loadTable "Packages"
        let task = table.GetItemAsync(Primitive.op_Implicit packageId)

        match task.Result with
        | null -> None
        | doc -> Some (packageOfDocument doc)

    let documentOfPackage (package:Package) =
        let packageItem = Document()
        packageItem.["Id"] <- v2.ConvertToEntry  package.Dsc.PackageId
        packageItem.["Name"] <- v2.ConvertToEntry package.Dsc.Name
        packageItem.["Producer"] <- v2.ConvertToEntry package.Dsc.Producer
        packageItem.["TransferToken"] <- v2.ConvertToEntry package.TransferToken

        let questionsEntry = DynamoDBList()
        for qw in package.Questions do
             let qwItem = Document()
             qwItem.["Text"] <- v2.ConvertToEntry qw.Text
             qwItem.["ImgKey"] <- v2.ConvertToEntry qw.ImgKey
             qwItem.["Answer"] <- v2.ConvertToEntry qw.Answer
             qwItem.["Comment"] <- v2.ConvertToEntry qw.Comment
             qwItem.["CommentImgKey"] <- v2.ConvertToEntry qw.CommentImgKey
             questionsEntry.Add(qwItem)

        packageItem.["Questions"] <- questionsEntry
        packageItem.["Version"] <- v2.ConvertToEntry package.Version

        packageItem

    let private descriptorOfDocument (doc:Document) : PackageDescriptor =
        let packageId = doc.["Id"].AsInt()
        let producer = stringOfDoc doc "Producer"
        let name = stringOfDoc doc "Name"

        {PackageId = packageId; Producer = producer; Name = name}

    let packageOfDocument (doc:Document) =
        let dsc = descriptorOfDocument doc
        let transferToken = stringOfDoc doc "TransferToken"
        let version = doc.["Version"].AsInt()

        let qwList = doc.["Questions"].AsListOfDocument()
        let questions =
            qwList
            |> Seq.map packageQuestionOfDocument
            |> List.ofSeq

        {Dsc = dsc; Version = version; Questions = questions; TransferToken = transferToken}

    let packageQuestionOfDocument  (qwDoc:Document) =
        let qwText = stringOfDoc qwDoc "Text"
        let qwImgKey = stringOfDoc qwDoc "ImgKey"
        let qwAnswer = stringOfDoc qwDoc "Answer"
        let qwComment = stringOfDoc qwDoc "Comment"
        let qwCommentImgKey = stringOfDoc qwDoc "CommentImgKey"

        {
            Text = qwText
            ImgKey = qwImgKey
            Answer = qwAnswer
            Comment = qwComment
            CommentImgKey = qwCommentImgKey
        }

