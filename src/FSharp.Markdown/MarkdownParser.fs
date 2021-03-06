﻿// --------------------------------------------------------------------------------------
// F# Markdown (MarkdownParser.fs)
// (c) Tomas Petricek, 2012, Available under Apache 2.0 license.
// --------------------------------------------------------------------------------------

module internal FSharp.Markdown.Parser

open System
open System.Collections.Generic
open System.Text.RegularExpressions

open FSharp.Patterns
open FSharp.Collections

// --------------------------------------------------------------------------------------
// Parsing of Markdown - first part handles inline formatting
// --------------------------------------------------------------------------------------

/// Splits a link formatted as `http://link "title"` into a link part
/// and an optional title part (may be wrapped using quote or double-quotes)
let getLinkAndTitle (String.TrimBoth input) =
  let url, title =
    if input.Length = 0 then "", None else
    let c = input.[input.Length - 1]
    if c = '\'' || c = '"' then
      let start = input.IndexOf(c)
      input.Substring(0, start).Trim(), Some(input.Substring(start + 1, input.Length - 2 - start).Trim())
    else input, None
  url.TrimStart('<').TrimEnd('>'), title

/// Succeeds when the specified character list starts with an escaped
/// character - in that case, returns the character and the tail of the list
let inline (|EscapedChar|_|) input =
  match input with
  | '\\'::( ( '*' | '\\' | '`' | '_' | '{' | '}' | '[' | ']'
            | '(' | ')' | '>' | '#' | '.' | '!' | '+' | '-' | '$') as c) ::rest -> Some(c, rest)
  | _ -> None

/// Escape dollar inside a LaTex inline math span.
let inline (|EscapedLatexInlineMathChar|_|) input =
  match input with
  | '\\'::( ('$') as c) :: rest -> Some(c, rest)
  | _ -> None

/// Matches a list if it starts with a sub-list that is delimited
/// using the specified delimiters. Returns a wrapped list and the rest.
///
/// This is similar to `List.Delimited`, but it skips over escaped characters.
let (|DelimitedMarkdown|_|) bracket input =
  let startl, endl = bracket, bracket
  // Like List.partitionUntilEquals, but skip over escaped characters
  let rec loop acc = function
    | EscapedChar(x, xs) -> loop (x::'\\'::acc) xs
    | input when List.startsWith endl input -> Some(List.rev acc, input)
    | x::xs -> loop (x::acc) xs
    | [] -> None
  // If it starts with 'startl', let's search for 'endl'
  if List.startsWith bracket input then
    match loop [] (List.skip bracket.Length input) with
    | Some(pre, post) -> Some(pre, List.skip bracket.Length post)
    | None -> None
  else None


/// This is similar to `List.Delimited`, but it skips over Latex inline math characters.
let (|DelimitedLatexDisplayMath|_|) bracket input =
  let startl, endl = bracket, bracket
  // Like List.partitionUntilEquals, but skip over escaped characters
  let rec loop acc = function
    | EscapedLatexInlineMathChar(x, xs) -> loop (x::'\\'::acc) xs
    | input when List.startsWith endl input -> Some(List.rev acc, input)
    | x::xs -> loop (x::acc) xs
    | [] -> None
  // If it starts with 'startl', let's search for 'endl'
  if List.startsWith bracket input then
    match loop [] (List.skip bracket.Length input) with
    | Some(pre, post) -> Some(pre, List.skip bracket.Length post)
    | None -> None
  else None

/// This is similar to `List.Delimited`, but it skips over Latex inline math characters.
let (|DelimitedLatexInlineMath|_|) bracket input =
  let startl, endl = bracket, bracket
  // Like List.partitionUntilEquals, but skip over escaped characters
  let rec loop acc = function
    | EscapedLatexInlineMathChar(x, xs) -> loop (x::'\\'::acc) xs
    | input when List.startsWith endl input -> Some(List.rev acc, input)
    | x::xs -> loop (x::acc) xs
    | [] -> None
  // If it starts with 'startl', let's search for 'endl'
  if List.startsWith bracket input then
    match loop [] (List.skip bracket.Length input) with
    | Some(pre, post) -> Some(pre, List.skip bracket.Length post)
    | None -> None
  else None

/// Recognizes an indirect link written using `[body][key]` or just `[key]`
/// The key can be preceeded by a space or other single whitespace thing.
let (|IndirectLink|_|) = function
  | List.BracketDelimited '[' ']' (body, '\r'::'\n'::(List.BracketDelimited '[' ']' (List.AsString link, rest))) ->
      Some(body, link, "\r\n[" + link + "]", rest)
  | List.BracketDelimited '[' ']' (body, ((' ' | '\n') as c)::(List.BracketDelimited '[' ']' (List.AsString link, rest))) ->
      Some(body, link, c.ToString() + "[" + link + "]", rest)
  | List.BracketDelimited '[' ']' (body, List.BracketDelimited '[' ']' (List.AsString link, rest)) ->
      Some(body, link, "[" + link + "]", rest)
  | List.BracketDelimited '[' ']' (body, rest) ->
      Some(body, "", "", rest)
  | _ -> None

/// Recognize a direct link written using `[body](http://url "with title")`
let (|DirectLink|_|) = function
  | List.BracketDelimited '[' ']' (body, List.BracketDelimited '(' ')' (link, rest)) ->
      Some(body, link, rest)
  | _ -> None

/// Recognizes an automatic link written using `http://url` or `https://url`
let (|AutoLink|_|) input =
  let linkFor (scheme:string) =
    let prefix = scheme.ToCharArray() |> Array.toList
    match input with
    | List.DelimitedWith prefix [' '] (List.AsString link, rest) ->
        Some(scheme + link, ' '::rest)
    | List.StartsWith prefix (List.AsString link) ->
        Some(link, [])
    | _ -> None

  ["http://";"https://"]
  |> Seq.tryPick linkFor

/// Recognizes some form of emphasis using `**bold**` or `*italic*`
/// (both can be also marked using underscore).
/// TODO: This does not handle nested emphasis well.
let (|Emphasised|_|) = function
  | (('_' | '*') :: tail) as input ->
    match input with
    | DelimitedMarkdown ['_'; '_'; '_'] (body, rest)
    | DelimitedMarkdown ['*'; '*'; '*'] (body, rest) ->
        Some(body, Emphasis >> List.singleton >> Strong, rest)
    | DelimitedMarkdown ['_'; '_'] (body, rest)
    | DelimitedMarkdown ['*'; '*'] (body, rest) ->
        Some(body, Strong, rest)
    | DelimitedMarkdown ['_'] (body, rest)
    | DelimitedMarkdown ['*'] (body, rest) ->
        Some(body, Emphasis, rest)
    | _ -> None
  | _ -> None

/// Parses a body of a paragraph and recognizes all inline tags.
let rec parseChars acc input = seq {

  // Zero or one literals, depending whether there is some accumulated input
  let accLiterals = Lazy.Create(fun () ->
    if List.isEmpty acc then []
    else [Literal(String(List.rev acc |> Array.ofList))] )

  match input with
  // Recognizes explicit line-break at the end of line
  | ' '::' '::'\r'::'\n'::rest
  | ' '::' '::('\n' | '\r')::rest ->
      yield! accLiterals.Value
      yield HardLineBreak
      yield! parseChars [] rest

  // Encode & as an HTML entity
  | '&'::'a'::'m'::'p'::';'::rest
  | '&'::rest ->
      yield! parseChars (';'::'p'::'m'::'a'::'&'::acc) rest

  // Ignore escaped characters that might mean something else
  | EscapedChar(c, rest) ->
      yield! parseChars (c::acc) rest

  // Inline code delimited either using double `` or single `
  // (if there are spaces around, then body can contain more backticks)
  | List.DelimitedWith ['`'; ' '] [' '; '`'] (body, rest)
  | List.DelimitedNTimes '`' (body, rest) ->
      yield! accLiterals.Value
      yield InlineCode(String(Array.ofList body).Trim())
      yield! parseChars [] rest

  // Display Latex inline math mode
  | DelimitedLatexDisplayMath ['$';'$'] (body, rest) ->
    yield! accLiterals.Value
    yield LatexDisplayMath(String(Array.ofList body).Trim())
    yield! parseChars [] rest

  // Inline Latex inline math mode
  | DelimitedLatexInlineMath ['$'] (body, rest) ->
    yield! accLiterals.Value
    yield LatexInlineMath(String(Array.ofList body).Trim())
    yield! parseChars [] rest

  // Inline link wrapped as <http://foo.bar>
  | List.DelimitedWith ['<'] ['>'] (List.AsString link, rest)
        when Seq.forall (Char.IsWhiteSpace >> not) link && (link.Contains("@") || link.Contains("://")) ->
      yield! accLiterals.Value
      yield DirectLink([Literal link], (link, None))
      yield! parseChars [] rest
  // Not an inline link - leave as an inline HTML tag
  | List.DelimitedWith ['<'] ['>'] (tag, rest) ->
      yield! parseChars ('>'::(List.rev tag) @ '<' :: acc) rest

  // Recognize direct link [foo](http://bar) or indirect link [foo][bar] or auto link http://bar
  | DirectLink (body, link, rest) ->
      yield! accLiterals.Value
      let info = getLinkAndTitle (String(Array.ofList link))
      yield DirectLink(parseChars [] body |> List.ofSeq, info)
      yield! parseChars [] rest
  | IndirectLink(body, link, original, rest) ->
      yield! accLiterals.Value
      let key = if String.IsNullOrEmpty(link) then String(body |> Array.ofSeq) else link
      yield IndirectLink(parseChars [] body |> List.ofSeq, original, key)
      yield! parseChars [] rest
  | AutoLink (link, rest) ->
      yield! accLiterals.Value
      yield DirectLink([Literal link], (link, None))
      yield! parseChars [] rest

  // Recognize image - this is a link prefixed with the '!' symbol
  | '!'::DirectLink (body, link, rest) ->
      yield! accLiterals.Value
      yield DirectImage(String(Array.ofList body), getLinkAndTitle (String(Array.ofList link)))
      yield! parseChars [] rest
  | '!'::IndirectLink(body, link, original, rest) ->
      yield! accLiterals.Value
      let key = if String.IsNullOrEmpty(link) then String(body |> Array.ofSeq) else link
      yield IndirectImage(String(Array.ofList body), original, key)
      yield! parseChars [] rest

  // Handle emphasised text
  | Emphasised (body, f, rest) ->
      yield! accLiterals.Value
      let body = parseChars [] body |> List.ofSeq
      yield f(body)
      yield! parseChars [] rest
  // Encode '<' char if it is not link or inline HTML
  | '<'::rest ->
      yield! parseChars (';'::'t'::'l'::'&'::acc) rest
  | '>'::rest ->
      yield! parseChars (';'::'t'::'g'::'&'::acc) rest
  | x::xs ->
      yield! parseChars (x::acc) xs
  | [] ->
      yield! accLiterals.Value }

/// Parse body of a paragraph into a list of Markdown inline spans
let parseSpans (String.TrimBoth s) =
  parseChars [] (s.ToCharArray() |> List.ofArray) |> List.ofSeq

let rec trimSpaces numSpaces (s:string) =
  if numSpaces <= 0 then s
  elif s.StartsWith(" ") then trimSpaces (numSpaces - 1) (s.Substring(1))
  elif s.StartsWith("\t") then trimSpaces (numSpaces - 4) (s.Substring(1))
  else s

// --------------------------------------------------------------------------------------
// Parsing of Markdown - second part handles paragraph-level formatting (headings, etc.)
// --------------------------------------------------------------------------------------

/// Recognizes heading, either prefixed with #s or followed by === or --- line
let (|Heading|_|) = function
  | (String.TrimBoth header) :: (String.TrimEnd (String.EqualsRepeated "=")) :: rest ->
      Some(1, header, rest)
  | (String.TrimBoth header) :: (String.TrimEnd (String.EqualsRepeated "-")) :: rest ->
      Some(2, header, rest)
  | String.StartsWithRepeated "#" (n, header) :: rest ->
      let header =
        // Drop "##" at the end, but only when it is preceded by some whitespace
        // (For example "## Hello F#" should be "Hello F#")
        if header.EndsWith "#" then
          let noHash = header.TrimEnd [| '#' |]
          if noHash.Length > 0 && Char.IsWhiteSpace(noHash.Chars(noHash.Length - 1))
          then noHash else header
        else header
      Some(n, header.Trim(), rest)
  | rest ->
      None

/// Recognizes a horizontal rule written using *, _ or -
let (|HorizontalRule|_|) (line:string) =
  let rec loop ((h, a, u) as arg) i =
    if (h >= 3 || a >= 3 || u >= 3) && i = line.Length then Some(line.[0])
    elif i = line.Length then None
    elif Char.IsWhiteSpace line.[i] then loop arg (i + 1)
    elif line.[i] = '-' && a = 0 && u = 0 then loop (h + 1, a, u) (i + 1)
    elif line.[i] = '*' && h = 0 && u = 0 then loop (h, a + 1, u) (i + 1)
    elif line.[i] = '_' && a = 0 && h = 0 then loop (h, a, u + 1) (i + 1)
    else None
  loop (0, 0, 0) 0

/// Recognizes a code block - lines starting with four spaces (including blank)
let (|NestedCodeBlock|_|) = function
  | Lines.TakeCodeBlock (numspaces, Lines.TrimBlank lines, rest) when lines <> [] ->
      let code =
        [ for l in lines ->
            if String.IsNullOrEmpty l then ""
            else trimSpaces 4 l ]
      Some(code @ [""], rest, "", "")
  | _ -> None

/// Recognizes a fenced code block - starting and ending with at least ``` or ~~~
let (|FencedCodeBlock|_|) = function
  | String.StartsWithNTimesTrimIgnoreStartWhitespace "~" (Let "~" (start,num), indent, header) :: lines
  //    when num > 2
  | String.StartsWithNTimesTrimIgnoreStartWhitespace "`" (Let "`" (start,num), indent, header) :: lines
      when num > 2 ->
    let mutable endStr = String.replicate num start
    if header.Contains (start) then None // info string cannot contain backspaces
    else
      let code, rest = lines |> List.partitionUntil (fun line ->
        match [line] with
        // end cannot contain info string afterwards (see http://spec.commonmark.org/0.23/#example-104)
        // end must be indended with less then 4 spaces: http://spec.commonmark.org/0.23/#example-95
        | String.StartsWithNTimesTrimIgnoreStartWhitespace start (n, i, h) :: _ when n >= num && i < 4 && String.IsNullOrWhiteSpace h ->
          endStr <- String.replicate n start
          true
        | _ -> false)
      let handleIndent (l:string) =
        if l.Length <= indent && String.IsNullOrWhiteSpace l then ""
        elif l.Length > indent && String.IsNullOrWhiteSpace (l.Substring(0, indent)) then l.Substring(indent, l.Length - indent)
        else l.TrimStart()
      let code =
        [ for l in code -> handleIndent l ]

      // langString is the part after ``` and ignoredString is the rest until the line ends.
      let langString, ignoredString =
        if String.IsNullOrWhiteSpace header then "", "" else
        let splits = header.Split((null : char array), StringSplitOptions.RemoveEmptyEntries)
        match splits |> Seq.tryFind (fun _ -> true) with
        | None -> "", ""
        | Some langString ->
            let ignoredString = header.Substring(header.IndexOf(langString) + langString.Length)
            langString, if String.IsNullOrWhiteSpace ignoredString then "" else ignoredString
      // Handle the ending line
      let code, rest =
        match rest with
        | hd :: tl ->
            let idx = hd.IndexOf(endStr)
            if idx > -1 && idx + endStr.Length <= hd.Length then
                let pre = hd.Substring(0, idx)
                let after = hd.Substring(idx + endStr.Length)
                code @ [""], (if String.IsNullOrWhiteSpace after then tl else after :: tl)
            else
                code @ [""], tl
        | _ ->
            code, rest
      Some (code, rest, langString, ignoredString)
  | _ -> None

/// Matches when the input starts with a number. Returns the
/// rest of the input, following the last number.
let (|SkipSomeNumbers|_|) (input:string) =
  match List.ofSeq input with
  | x::xs when Char.IsDigit x ->
      let _, rest = List.partitionUntil (Char.IsDigit >> not) xs
      Some(input.Length - rest.Length, rest)
  | _ -> None

/// Recognizes a staring of a list (either 1. or +, *, -).
/// Returns the rest of the line, together with the indent.
let (|ListStart|_|) = function
  | String.TrimStartAndCount
      (startIndent, spaces,
        // NOTE: a tab character after +, * or - isn't supported by the reference implementation
        // (it will be parsed as paragraph for 0.22)
        (String.StartsWithAny ["+ "; "* "; "- " (*; "+\t"; "*\t"; "-\t"*)] as item)) ->
      let li = item.Substring(2)
      let (String.TrimStartAndCount (startIndent2, spaces2, _)) = li
      let endIndent =
        startIndent + 2 +
        // Handle case of code block
        if startIndent2 >= 5 then 1 else startIndent2
      Some(Unordered, startIndent, endIndent, li)
  | String.TrimStartAndCount // Remove leading spaces
      (startIndent, spaces,
       (SkipSomeNumbers // read a number
          (skipNumCount, '.' :: ' ' :: List.AsString item))) ->
      let (String.TrimStartAndCount (startIndent2, spaces2, _)) = item
      let endIndent =
        startIndent + 2 + skipNumCount +
        // Handle case of code block
        if startIndent2 >= 5 then 1 else startIndent2
      Some(Ordered, startIndent, endIndent, item)
  | _ -> None

/// Splits input into lines until whitespace or starting of a list and the rest.
let (|LinesUntilListOrWhite|) =
  List.partitionUntil (function
    | ListStart _ | String.WhiteSpace -> true | _ -> false)

/// Splits input into lines until not-indented line or starting of a list and the rest.
let (|LinesUntilListOrUnindented|) =
  List.partitionUntilLookahead (function
    | (ListStart _ | String.Unindented)::_
    | String.WhiteSpace::String.WhiteSpace::_ -> true | _ -> false)

/// Recognizes a list item until the next list item (possibly nested) or end of a list.
/// The parameter specifies whether the previous line was simple (single-line not
/// separated by a white line - simple items are not wrapped in <p>)
let (|ListItem|_|) prevSimple = function
  | ListStart(kind, startIndent, endIndent, item)::
      // Take remaining lines that belong to the same item
      // (everything until an empty line or start of another list item)
      LinesUntilListOrWhite
        (continued,
            // Take more things that belong to the item -
            // the value 'more' will contain indented paragraphs
            (LinesUntilListOrUnindented (more, rest) as next)) ->
      let simple =
        match item with
        | String.TrimStartAndCount (_, spaces, _) when spaces >= 4->
          // Code Block
          false
        | _ ->
          match next, rest with
          | String.WhiteSpace::_, (ListStart _)::_ -> false
          | (ListStart _)::_, _ -> true
          | [], _ -> true
          | [ String.WhiteSpace ], _ -> true
          | String.WhiteSpace::String.WhiteSpace::_, _ -> true
          | _, String.Unindented::_ -> prevSimple
          | _, _ -> false

      let lines =
        [ yield item
          for line in continued do
            yield line.Trim()
          for line in more do
            let trimmed = trimSpaces endIndent line
            yield trimmed ]
            //let trimmed = line.TrimStart()
            //if trimmed.Length >= line.Length - endIndent then yield trimmed
            //else yield line.Substring(endIndent) ]
      Some(startIndent, (simple, kind, lines), rest)
  | _ -> None

/// Recognizes a list - returns list items with information about
/// their indents - these need to be turned into a tree structure later.
let rec (|ListItems|_|) prevSimple = function
  | ListItem prevSimple (indent, ((nextSimple, _, _) as info), rest) ->
      match rest with
      | (HorizontalRule _)::_ ->
          Some([indent, info], rest)
      | ListItems nextSimple (items, rest) ->
          Some((indent, info)::items, rest)
      | _ -> Some([indent, info], rest)
  | _ -> None


// Code for parsing pipe tables

// Splits table row into deliminated parts escaping inline code and latex
let rec pipeTableFindSplits (delim : char array) (line : char list) = 
    let cLstToStr (x : char list) = 
        x
        |> Array.ofList
        |> System.String.Concat
    
    let rec ptfs delim line = 
        match line with
        | DelimitedLatexDisplayMath [ '$'; '$' ] (body, rest) -> ptfs delim rest
        | DelimitedLatexInlineMath [ '$' ] (body, rest) -> ptfs delim rest
        | List.DelimitedWith [ '`'; ' ' ] [ ' '; '`' ] (body, rest) -> ptfs delim rest
        | List.DelimitedNTimes '`' (body, rest) -> ptfs delim rest
        | x :: rest when Array.exists ((=) x) delim -> Some rest
        | '\\' :: _ :: rest | _ :: rest -> ptfs delim rest
        | [] -> None
    
    let rest = ptfs delim line
    match rest with
    | None -> [ cLstToStr line ]
    | Some x when line = [] -> [ "" ]
    | Some x -> 
        let chunkSize = List.length line - List.length x - 1
        cLstToStr (Seq.take chunkSize line |> Seq.toList) :: pipeTableFindSplits delim x


    
    

/// Recognizes alignment specified in the passed separator line.
let (|TableCellSeparator|_|) = function
  | String.StartsAndEndsWith (":", ":") (String.EqualsRepeated "-") -> Some(AlignCenter)
  | String.StartsWith ":" (String.EqualsRepeated "-") -> Some(AlignLeft)
  | String.StartsAndEndsWith ("", ":") (String.EqualsRepeated "-") -> Some(AlignRight)
  | String.EqualsRepeated "-" -> Some(AlignDefault)
  | _ -> None

/// Recognizes row of pipe table.
/// The function takes number of expected columns and array of delimiters.
/// Returns list of strings between delimiters.
let (|PipeTableRow|_|) (size : option<int>) delimiters (line : string) = 
    let parts = 
        pipeTableFindSplits delimiters (line.ToCharArray() |> Array.toList)
        |> List.toArray
        |> Array.map (fun s -> s.Trim())
    
    let n = parts.Length
    
    let m = 
        if size.IsNone then 1
        else size.Value
    
    let x = 
        if String.IsNullOrEmpty parts.[0] && n > m then 1
        else 0
    
    let y = 
        if String.IsNullOrEmpty parts.[n - 1] && n - x > m then n - 2
        else n - 1
    
    if n = 1 || (size.IsSome && y - x + 1 <> m) then None
    else Some(parts.[x..y] |> Array.toList)


/// Recognizes separator row of pipe table.
/// Returns list of alignments.
let (|PipeSeparatorRow|_|) size = function
  | PipeTableRow size [|'|'; '+'|] parts ->
      let alignments = parts |> List.choose ( |TableCellSeparator|_| )
      if parts.Length <> alignments.Length then None else (Some alignments)
  | _ -> None

/// Recognizes pipe table
let (|PipeTableBlock|_|) input =
  let rec getTableRows size acc = function
    | (PipeTableRow size [|'|'|] columns) :: rest ->
        getTableRows size (List.map (fun l -> [l]) columns :: acc) rest
    | rest -> (List.rev acc, rest)
  match input with
  | (PipeSeparatorRow None alignments) :: rest ->
      let rows, others = getTableRows (Some alignments.Length) [] rest
      Some((None, alignments, rows), others)
  | (PipeTableRow None [|'|'|] headers) :: rest ->
      match rest with
      | (PipeSeparatorRow (Some headers.Length) alignments) :: rest ->
          let rows, others = getTableRows (Some headers.Length) [] rest
          let header_paragraphs = headers |> List.map (fun l -> [l])
          Some((Some(header_paragraphs), alignments, rows), others)
      | _ -> None
  | _ -> None

// Code for parsing emacs tables

/// Recognizes one line of emacs table. It can be line with content or separator line.
/// The function takes positions of grid columns (if known) and expected grid separator.
/// Passed function is used to check whether all parts within grid are valid.
/// Retuns tuple (position of grid columns, text between grid columns).
let (|EmacsTableLine|_|) (grid:option<int []>) (c:char) (check:string -> bool) (line:string) =
  let p = if grid.IsSome then grid.Value else Array.FindAll([|0..line.Length - 1|], fun i -> line.[i] = c)
  let n = p.Length - 1
  if n < 2 || line.Length <= p.[n] || Array.exists (fun i -> line.[i] <> c) p then None
  else
    let parts = [1..n] |> List.map (fun i -> line.Substring(p.[i - 1] + 1, p.[i] - p.[i - 1] - 1))
    if List.forall check parts then Some(p, parts) else None

/// Recognizes emacs table
let (|EmacsTableBlock|_|) input =
  let isCellSep = String.(|EqualsRepeated|_|) "-" >> Option.isSome
  let isAlignedCellSep = ( |TableCellSeparator|_| ) >> Option.isSome
  let isHeadCellSep = String.(|EqualsRepeated|_|) "=" >> Option.isSome
  let isText (s:string) = true
  match input with
  | (EmacsTableLine None '+' isAlignedCellSep (grid, parts)) :: rest ->
    let alignments = List.choose ( |TableCellSeparator|_| ) parts
    // iterate over rows and go from state to state
    // headers - the content of head row (initially none)
    // prevRow - content of the processed rows
    // cur - list of paragraphs in the current row (list of empty lists after each separator line)
    // flag indicates whether current row is empty (similar to List.forall (List.isEmpty) cur)
    let emptyCur = List.replicate<string list> (grid.Length - 1) []
    let rec loop flag headers prevRows cur = function
      | (EmacsTableLine (Some grid) '|' isText (_, parts)) :: others ->
          loop false headers prevRows (List.zip parts cur |> List.map (fun (h, t) -> h.TrimEnd() :: t)) others
      | (EmacsTableLine (Some grid) '+' isCellSep _) :: others ->
          loop true headers (List.map (List.rev) cur :: prevRows) emptyCur others
      | (EmacsTableLine (Some grid) '+' isHeadCellSep _) :: others when Option.isNone headers ->
          loop true (Some (List.map (List.rev) cur)) prevRows emptyCur others
      | others when flag -> Some((headers, alignments, List.rev prevRows), others)
      | _ -> None
    loop true None [] emptyCur rest
  | _ -> None

/// Recognizes a start of a blockquote
let (|BlockquoteStart|_|) (line:string) =
  let regex =
    "^ {0,3}" // Up to three leading spaces
    + ">" // Blockquote character
    + "\s?" // Maybe one whitespace character
    + "(.*)" // Capture everything else
  let match' = Regex.Match(line, regex)
  if match'.Success then Some (match'.Groups.Item(1)).Value
  else None

/// Takes lines that belong to a continuing paragraph until
/// a white line or start of other paragraph-item is found
let (|TakeParagraphLines|_|) input =
  match List.partitionWhileLookahead (function
    | Heading _ -> false
    | FencedCodeBlock _ -> false
    | BlockquoteStart _::_ -> false
    | String.WhiteSpace::_ -> false
    | _ -> true) input with
  | matching, rest when matching <> [] -> Some(matching, rest)
  | _ -> None

/// Recognize nested HTML block
/// TODO: This is too simple - takes paragraph that starts with <
let (|HtmlBlock|_|) = function
  | first::_ & TakeParagraphLines(html, rest) when first.StartsWith("<") ->
      Some(html, rest)
  | _ -> None

/// Continues taking lines until a whitespace line or start of a blockquote
let (|LinesUntilBlockquoteEnds|) input =
  List.partitionUntilLookahead (fun next ->
    match next with
    | BlockquoteStart _ :: _
    | Heading _
    | String.WhiteSpace :: _ -> true
    | _ ->
      false) input

/// Recognizes blockquote - continues taking paragraphs
/// starting with '>' until there is something else
let rec (|Blockquote|_|) = function
  | EmptyBlockquote(Lines.TrimBlankStart rest) ->
      Some ([""], rest)
  | BlockquoteStart(line)::LinesUntilBlockquoteEnds(continued, Lines.TrimBlankStart rest) ->
      let moreLines, rest =
        match rest with
        | Blockquote(lines, rest) -> lines, rest
        | _ -> [], rest
      Some (line::continued @ moreLines, rest)
  | _ -> None

/// Recognizes a special case: an empty blockquote line should terminate
/// the blockquote if the next line is not a blockquote
and (|EmptyBlockquote|_|) = function
  | BlockquoteStart(String.WhiteSpace) :: Blockquote(_) -> None
  | BlockquoteStart(String.WhiteSpace) :: rest -> Some rest
  | _ -> None

/// Recognizes Latex block - start with "$$$"
let (|LatexBlock|_|) (lines:string list) = lines |> function
  | first::rest when (first.TrimEnd()) = "$$$" -> rest |> function
    | TakeParagraphLines(body, rest) -> Some(body, rest)
    | _ -> None
  | _ -> None

/// Recognize a definition of a link as in `[key]: http://url ...`
let (|LinkDefinition|_|) = function
  | ( String.StartsWithWrapped ("[", "]:") (wrapped, String.TrimBoth link)
    | String.StartsWithWrapped (" [", "]:") (wrapped, String.TrimBoth link)
    | String.StartsWithWrapped ("  [", "]:") (wrapped, String.TrimBoth link)
    | String.StartsWithWrapped ("   [", "]:") (wrapped, String.TrimBoth link) ) :: rest ->
        Some((wrapped, link), rest)
  | _ -> None

/// Defines a context for the main `parseParagraphs` function
type ParsingContext =
  { Links : Dictionary<string, string * option<string>>
    Newline : string }

/// Parse a list of lines into a sequence of markdown paragraphs
let rec parseParagraphs (ctx:ParsingContext) lines = seq {
  match lines with
  // Recognize various kinds of standard paragraphs
  | LinkDefinition ((key, link), Lines.TrimBlankStart lines) ->
      ctx.Links.Add(key, getLinkAndTitle link)
      yield! parseParagraphs ctx lines
  | NestedCodeBlock(code, Lines.TrimBlankStart lines, langString, ignoredLine)
  | FencedCodeBlock(code, Lines.TrimBlankStart lines, langString, ignoredLine) ->
      yield CodeBlock(code |> String.concat ctx.Newline, langString, ignoredLine)
      yield! parseParagraphs ctx lines
  | Blockquote(body, Lines.TrimBlankStart rest) ->
      yield QuotedBlock(parseParagraphs ctx (body @ [""]) |> List.ofSeq)
      yield! parseParagraphs ctx rest
  | EmacsTableBlock((headers, alignments, rows), Lines.TrimBlankStart rest)
  | PipeTableBlock((headers, alignments, rows), Lines.TrimBlankStart rest) ->
      let headParagraphs =
        if headers.IsNone then None
        else Some(headers.Value |> List.map (fun i -> parseParagraphs ctx i |> List.ofSeq))
      yield TableBlock(headParagraphs, alignments,
        rows |> List.map (List.map (fun i -> parseParagraphs ctx i |> List.ofSeq)))
      yield! parseParagraphs ctx rest
  | HorizontalRule(c) :: (Lines.TrimBlankStart lines) ->
      yield HorizontalRule(c)
      yield! parseParagraphs ctx lines
  | LatexBlock(body, Lines.TrimBlankStart rest) ->
    yield LatexBlock(body)
    yield! parseParagraphs ctx rest


  // Recognize list of list items and turn it into nested lists
  | ListItems true (items, Lines.TrimBlankStart rest) ->
      let tree = Tree.ofIndentedList items

      // Nest all items that have another kind (i.e. UL vs. OL)
      let rec nestUnmatchingItems items =
        match items with
        | Node((_, baseKind, _), _)::_ ->
            items
            |> List.nestUnderLastMatching (fun (Node((_, kind, _), _)) -> kind = baseKind)
            |> List.map (fun (Node(info, children), nested) ->
                let children = nestUnmatchingItems children
                Node(info, children @ nested))
        | [] -> []

      // Turn tree into nested list definitions
      let rec formatTree (nodes:Tree<bool * MarkdownListKind * string list> list) =
        let kind = match nodes with Node((_, kind, _), _)::_ -> kind | _ -> Unordered
        let items =
          [ for (Node((simple, _, body), nested)) in nodes ->
              [ if not simple then yield! parseParagraphs ctx body
                else yield Span(parseSpans(String.concat ctx.Newline body))
                if nested <> [] then
                  yield formatTree nested ] ]
        ListBlock(kind, items)

      // Make sure all items of the list have are either simple or not.
      let rec unifySimpleProperty (nodes:Tree<bool * MarkdownListKind * string list> list) =
        let containsNonSimple =
          tree |> Seq.exists (function
            | Node ((false, _, _), _) -> true
            | _ -> false)
        if containsNonSimple then
          nodes |> List.map (function
            | Node ((_, kind, content), nested) -> Node((false, kind, content), unifySimpleProperty nested))
        else nodes

      yield  tree |> unifySimpleProperty |> formatTree
      yield! parseParagraphs ctx rest

  // Recognize remaining types of paragraphs
  | Heading(n, body, Lines.TrimBlankStart lines) ->
      yield Heading(n, parseSpans body)
      yield! parseParagraphs ctx lines
  | HtmlBlock(code, Lines.TrimBlankStart lines) when
        ( let all = String.concat ctx.Newline code
          not (all.StartsWith("<http://")) && not (all.StartsWith("<ftp://")) && not (all.Contains("@")) ) ->
      let all = String.concat ctx.Newline code
      yield InlineBlock(all)
      yield! parseParagraphs ctx lines
  | TakeParagraphLines(Lines.TrimParagraphLines lines, Lines.TrimBlankStart rest) ->
      yield Paragraph (parseSpans (String.concat ctx.Newline lines))
      yield! parseParagraphs ctx rest

  | Lines.TrimBlankStart [] -> ()
  | _ -> failwithf "Unexpectedly stopped!\n%A" lines }
