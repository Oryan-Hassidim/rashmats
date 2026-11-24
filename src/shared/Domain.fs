namespace Rashmatz.Shared

open System

/// Pragmatic Scott Wlaschin Domain Modeling:
/// Single-case [<Struct>] IDs + constrained values + sum types that make illegal states unrepresentable.
[<AutoOpen>]
module Domain =

    [<Struct>]
    type OrgId =
        | OrgId of Guid
        member x.Str = let (OrgId g) = x in g.ToString("D")

    [<Struct>]
    type UserId =
        | UserId of Guid
        member x.Value = let (UserId g) = x in g.ToString("D")

    [<Struct>]
    type HolderId =
        | HolderId of Guid
        member x.Str = let (HolderId g) = x in g.ToString("D")

    [<Struct>]
    type CategoryId =
        | CategoryId of Guid
        member x.Str = let (CategoryId g) = x in g.ToString("D")

    [<Struct>]
    type ItemTypeId =
        | ItemTypeId of Guid
        member x.Str = let (ItemTypeId g) = x in g.ToString("D")

    [<Struct>]
    type SerializedItemId =
        | SerializedItemId of Guid
        member x.Str = let (SerializedItemId g) = x in g.ToString("D")

    [<Struct>]
    type TemplateId =
        | TemplateId of Guid
        member x.Str = let (TemplateId g) = x in g.ToString("D")

    [<Struct>]
    type AuditSessionId =
        | AuditSessionId of Guid
        member x.Str = let (AuditSessionId g) = x in g.ToString("D")

    [<Struct>]
    type NonEmptyText =
        private
        | NonEmptyText of string
        member x.Value = let (NonEmptyText s) = x in s

    [<Struct>]
    type Barcode =
        private
        | Barcode of string
        member x.Value = let (Barcode s) = x in s

    [<Struct>]
    type PositiveQty =
        private
        | PositiveQty of int
        member x.Value = let (PositiveQty q) = x in q

    type SystemRole =
        | NoAccess = 0
        | Member = 1
        | Admin = 2

    type AttendanceStatus =
        | Inactive = 0
        | Present = 1
        | HomeRemote = 2

    type TrackingType =
        | Quantitative = 0
        | Serialized = 1

    type AuditSessionStatus =
        | Open = 0
        | Closed = 1
        | Archived = 2

    type AuditScanStatus =
        | Pending = 0
        | Verified = 1
        | Mismatch = 2
        | Unexpected = 3

    /// Enforces Flexible OR Principle: a physical location must specify a holder, custom text, or both.
    type PhysicalLocation =
        | AtHolder of HolderId
        | CustomText of NonEmptyText
        | AtHolderAndText of HolderId * NonEmptyText

        member x.ToDbCols() =
            match x with
            | AtHolder h -> Some h.Str, None
            | CustomText t -> None, Some t.Value
            | AtHolderAndText (h, t) -> Some h.Str, Some t.Value

    /// Null-safe trim for .NET 11 `<Nullable>enable</Nullable>`.
    let trimNullable (raw: string | null) =
        match raw with
        | null -> None
        | s ->
            let t = s.Trim()

            if String.IsNullOrEmpty t then
                None
            else
                Some t

    let trimOpt (raw: string option) =
        raw |> Option.bind (fun s -> trimNullable s)

    module Parse =
        let guid field (raw: string) =
            if String.IsNullOrWhiteSpace raw then
                Error(ApiError.field field ApiErrorCode.EmptyRequiredField)
            else
                match Guid.TryParse(raw.Trim()) with
                | true, g when g <> Guid.Empty -> Ok g
                | _ -> Error(ApiError.field field ApiErrorCode.InvalidUuidFormat)

        let optGuid field (raw: string option) =
            match trimOpt raw with
            | None -> Ok None
            | Some s -> guid field s |> Result.map Some

        let holderId field raw = guid field raw |> Result.map HolderId

        let optHolderId field raw =
            optGuid field raw
            |> Result.map (Option.map HolderId)

        let itemTypeId field raw = guid field raw |> Result.map ItemTypeId

        let serializedItemId field raw =
            guid field raw |> Result.map SerializedItemId

        let optTemplateId field raw =
            optGuid field raw
            |> Result.map (Option.map TemplateId)

        let nonEmpty field (raw: string option) =
            match trimOpt raw with
            | Some s -> s |> NonEmptyText |> Ok
            | None -> Error(ApiError.field field ApiErrorCode.EmptyRequiredField)

        let barcode field raw =
            nonEmpty field raw
            |> Result.map (fun t -> Barcode t.Value)

        let positiveQty field qty =
            if qty > 0 then
                Ok(PositiveQty qty)
            else
                Error(ApiError.field field ApiErrorCode.InvalidQuantity)

        let location holderRaw textRaw =
            optHolderId "to_location_holder_id" holderRaw
            |> Result.bind (fun holderOpt ->
                match holderOpt, trimOpt textRaw with
                | Some h, Some t -> Ok(AtHolderAndText(h, NonEmptyText t))
                | Some h, None -> Ok(AtHolder h)
                | None, Some t -> Ok(CustomText(NonEmptyText t))
                | None, None -> Error(ApiError.field "to_location" ApiErrorCode.MissingLocationTarget))
