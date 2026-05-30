namespace FSharp.ExpressionEvaluator

open System

module ExpressionLowering =

    let private supportedModules =
        readOnlyDict [
            "Seq",  "Microsoft.FSharp.Collections.SeqModule"
            "List", "Microsoft.FSharp.Collections.ListModule"
        ]

    let private trim (text: string) = text.Trim()

    let private isEscaped (text: string) (index: int) =
        let mutable backslashes = 0
        let mutable i = index - 1
        while i >= 0 && text.[i] = '\\' do
            backslashes <- backslashes + 1
            i <- i - 1
        backslashes % 2 = 1

    let private findMatching (text: string) (startIndex: int) (openChar: char) (closeChar: char) =
        let mutable depth = 0
        let mutable inString = false
        let mutable result = -1
        let mutable i = startIndex

        while i < text.Length && result = -1 do
            let ch = text.[i]
            if ch = '"' && not (isEscaped text i) then
                inString <- not inString
            elif not inString then
                if ch = openChar then
                    depth <- depth + 1
                elif ch = closeChar then
                    depth <- depth - 1
                    if depth = 0 then
                        result <- i
            i <- i + 1

        result

    let private splitTopLevel (separator: char) (text: string) =
        let parts = ResizeArray<string>()
        let mutable start = 0
        let mutable parenDepth = 0
        let mutable bracketDepth = 0
        let mutable braceDepth = 0
        let mutable inString = false

        for i = 0 to text.Length - 1 do
            let ch = text.[i]
            if ch = '"' && not (isEscaped text i) then
                inString <- not inString
            elif not inString then
                match ch with
                | '(' -> parenDepth <- parenDepth + 1
                | ')' -> parenDepth <- parenDepth - 1
                | '[' -> bracketDepth <- bracketDepth + 1
                | ']' -> bracketDepth <- bracketDepth - 1
                | '{' -> braceDepth <- braceDepth + 1
                | '}' -> braceDepth <- braceDepth - 1
                | _ -> ()

                if ch = separator && parenDepth = 0 && bracketDepth = 0 && braceDepth = 0 then
                    parts.Add(text.Substring(start, i - start).Trim())
                    start <- i + 1

        parts.Add(text.Substring(start).Trim())
        parts |> Seq.toList

    let private splitTopLevelWhitespace (text: string) =
        let parts = ResizeArray<string>()
        let mutable current = System.Text.StringBuilder()
        let mutable parenDepth = 0
        let mutable bracketDepth = 0
        let mutable braceDepth = 0
        let mutable inString = false

        let flush () =
            if current.Length > 0 then
                parts.Add(current.ToString())
                current <- System.Text.StringBuilder()

        for i = 0 to text.Length - 1 do
            let ch = text.[i]
            if ch = '"' && not (isEscaped text i) then
                inString <- not inString
                current.Append(ch) |> ignore
            elif not inString then
                match ch with
                | '(' ->
                    parenDepth <- parenDepth + 1
                    current.Append(ch) |> ignore
                | ')' ->
                    parenDepth <- parenDepth - 1
                    current.Append(ch) |> ignore
                | '[' ->
                    bracketDepth <- bracketDepth + 1
                    current.Append(ch) |> ignore
                | ']' ->
                    bracketDepth <- bracketDepth - 1
                    current.Append(ch) |> ignore
                | '{' ->
                    braceDepth <- braceDepth + 1
                    current.Append(ch) |> ignore
                | '}' ->
                    braceDepth <- braceDepth - 1
                    current.Append(ch) |> ignore
                | c when Char.IsWhiteSpace(c) && parenDepth = 0 && bracketDepth = 0 && braceDepth = 0 ->
                    flush ()
                | _ ->
                    current.Append(ch) |> ignore
            else
                current.Append(ch) |> ignore

        flush ()
        parts |> Seq.toList

    let private isWrapped (text: string) (openChar: char) (closeChar: char) =
        text.Length >= 2 &&
        text.[0] = openChar &&
        text.[text.Length - 1] = closeChar &&
        findMatching text 0 openChar closeChar = text.Length - 1

    let private toClrModuleFunctionName (text: string) =
        let dot = text.IndexOf('.')
        if dot <= 0 || dot = text.Length - 1 then
            None
        else
            let moduleName = text.Substring(0, dot)
            let functionName = text.Substring(dot + 1)
            match supportedModules.TryGetValue(moduleName) with
            | true, clrModule when functionName.Length > 0 ->
                let clrFunction = Char.ToUpperInvariant(functionName.[0]).ToString() + functionName.Substring(1)
                Some (clrModule + "." + clrFunction)
            | _ ->
                None

    let rec private lowerExpressionInternal (text: string) : string option =
        let text = trim text
        if String.IsNullOrWhiteSpace(text) then
            None
        else
            let commaParts = splitTopLevel ',' text
            if commaParts.Length > 1 then
                commaParts
                |> List.map lowerExpressionInternal
                |> List.fold (fun state part ->
                    match state, part with
                    | Some acc, Some value -> Some (value :: acc)
                    | _ -> None) (Some [])
                |> Option.map (fun values -> values |> List.rev |> String.concat ", ")
            elif isWrapped text '(' ')' then
                let inner = text.Substring(1, text.Length - 2)
                lowerExpressionInternal inner |> Option.map (fun lowered -> "(" + lowered + ")")
            else
                lowerApplicationOrTerm text

    and private lowerApplicationOrTerm (text: string) : string option =
        let terms = splitTopLevelWhitespace text
        match terms with
        | [] -> None
        | [ single ] -> lowerTerm single
        | head :: tail ->
            match toClrModuleFunctionName head with
            | Some clrName ->
                tail
                |> List.map lowerExpressionInternal
                |> List.fold (fun state part ->
                    match state, part with
                    | Some acc, Some value -> Some (value :: acc)
                    | _ -> None) (Some [])
                |> Option.map (fun args ->
                    let args = args |> List.rev |> String.concat ", "
                    clrName + "(" + args + ")")
            | None ->
                None

    and private lowerTerm (text: string) : string option =
        let text = trim text
        if String.IsNullOrWhiteSpace(text) then
            None
        elif isWrapped text '(' ')' then
            let inner = text.Substring(1, text.Length - 2)
            lowerExpressionInternal inner |> Option.map (fun lowered -> "(" + lowered + ")")
        else
            lowerIndexers text

    and private lowerIndexers (text: string) : string option =
        let marker = text.IndexOf(".[")
        if marker < 0 then
            Some text
        else
            let baseText = text.Substring(0, marker)
            let bracketStart = marker + 1
            let bracketEnd = findMatching text bracketStart '[' ']'
            if String.IsNullOrWhiteSpace(baseText) || bracketEnd < 0 then
                None
            else
                match lowerTerm baseText, lowerExpressionInternal (text.Substring(bracketStart + 1, bracketEnd - bracketStart - 1)) with
                | Some loweredBase, Some loweredIndex ->
                    let lowered = loweredBase + "[" + loweredIndex + "]"
                    if bracketEnd = text.Length - 1 then
                        Some lowered
                    else
                        let suffix = text.Substring(bracketEnd + 1)
                        lowerIndexers (lowered + suffix)
                | _ ->
                    None

    let tryLowerExpression (text: string) : string option =
        lowerExpressionInternal text
