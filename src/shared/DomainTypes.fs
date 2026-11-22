namespace Rashmatz.Shared

open System

/// Pragmatic Scott Wlaschin Domain Modeling ("Domain Modeling Made Functional"):
/// 1. [<Struct>] Single-Case Discriminated Unions for domain identifiers so IDs cannot be mixed up.
/// 2. Constrained value types with smart constructors returning Result<T, FieldValidationError>.
/// 3. State machines and sum types that make illegal domain states unrepresentable.
module DomainTypes =

    [<Struct>]
    type OrgId = OrgId of Guid with
        member this.Value = let (OrgId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type UserId = UserId of Guid with
        member this.Value = let (UserId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type HolderId = HolderId of Guid with
        member this.Value = let (HolderId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type CategoryId = CategoryId of Guid with
        member this.Value = let (CategoryId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type ItemTypeId = ItemTypeId of Guid with
        member this.Value = let (ItemTypeId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type SerializedItemId = SerializedItemId of Guid with
        member this.Value = let (SerializedItemId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type TemplateId = TemplateId of Guid with
        member this.Value = let (TemplateId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type TransferId = TransferId of Guid with
        member this.Value = let (TransferId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    [<Struct>]
    type AuditSessionId = AuditSessionId of Guid with
        member this.Value = let (AuditSessionId v) = this in v
        member this.ToDbString() = this.Value.ToString("D")

    // Constrained Strings & Quantities
    [<Struct>]
    type NonEmptyString = private NonEmptyString of string with
        member this.Value = let (NonEmptyString s) = this in s

    [<Struct>]
    type Barcode = private Barcode of string with
        member this.Value = let (Barcode s) = this in s

    [<Struct>]
    type PositiveQuantity = private PositiveQuantity of int with
        member this.Value = let (PositiveQuantity q) = this in q

    // Numeric Enums matching db model.md (1-2 byte integers in DB, zero display strings)
    [<RequireQualifiedAccess>]
    type HolderType =
        | Location = 0
        | Person = 1

    [<RequireQualifiedAccess>]
    type SystemRole =
        | NoAccess = 0
        | Member = 1
        | Admin = 2

    [<RequireQualifiedAccess>]
    type AttendanceStatus =
        | Inactive = 0
        | Present = 1
        | HomeRemote = 2

    [<RequireQualifiedAccess>]
    type TrackingType =
        | Quantitative = 0
        | Serialized = 1

    [<RequireQualifiedAccess>]
    type ItemOperationalStatus =
        | Active = 0
        | Maintenance = 1
        | Lost = 2
        | Decommissioned = 3

    [<RequireQualifiedAccess>]
    type AuditSessionStatus =
        | Open = 0
        | Closed = 1
        | Archived = 2

    [<RequireQualifiedAccess>]
    type AuditScanStatus =
        | Pending = 0
        | Verified = 1
        | Mismatch = 2
        | Unexpected = 3

    /// Flexible OR Principle for Physical Location (db model.md Section 1.5 & 7):
    /// A physical location MUST have either a registered location holder, custom text, or both.
    /// Making "neither" unrepresentable in the domain!
    type PhysicalLocationTarget =
        | AtHolderOnly of holderId: HolderId
        | CustomTextOnly of text: NonEmptyString
        | AtHolderWithText of holderId: HolderId * text: NonEmptyString

        member this.ToDbColumns() : string option * string option =
            match this with
            | AtHolderOnly h -> Some(h.ToDbString()), None
            | CustomTextOnly t -> None, Some t.Value
            | AtHolderWithText(h, t) -> Some(h.ToDbString()), Some t.Value

    /// Null-safe helper for .NET 11 `<Nullable>enable</Nullable>` interop (`string | null -> string option`).
    let ofNullableTrimmed (raw: string | null) : string option =
        match raw with
        | null -> None
        | s ->
            let trimmed = s.Trim()
            if String.IsNullOrEmpty(trimmed) then None else Some trimmed

    /// Smart constructors for validation { ... } and result { ... } blocks
    module SmartConstructors =

        let parseGuid (fieldName: string) (raw: string) : Result<Guid, FieldValidationError> =
            if String.IsNullOrWhiteSpace(raw) then
                Error(ApiError.fieldError fieldName ApiErrorCode.EmptyRequiredField)
            else
                match Guid.TryParse(raw.Trim()) with
                | true, g when g <> Guid.Empty -> Ok g
                | _ -> Error(ApiError.fieldError fieldName ApiErrorCode.InvalidUuidFormat)

        let parseOptionalGuid (fieldName: string) (raw: string option) : Result<Guid option, FieldValidationError> =
            match raw with
            | None -> Ok None
            | Some s when String.IsNullOrWhiteSpace(s) -> Ok None
            | Some s -> parseGuid fieldName s |> Result.map Some

        let createOptionalGuid (fieldName: string) (raw: string option) : Result<Guid option, FieldValidationError> =
            parseOptionalGuid fieldName raw

        let createOrgId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map OrgId

        let createHolderId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map HolderId

        let createOptionalHolderId (fieldName: string) (raw: string option) =
            parseOptionalGuid fieldName raw |> Result.map (Option.map HolderId)

        let createCategoryId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map CategoryId

        let createOptionalCategoryId (fieldName: string) (raw: string option) =
            parseOptionalGuid fieldName raw |> Result.map (Option.map CategoryId)

        let createItemTypeId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map ItemTypeId

        let createSerializedItemId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map SerializedItemId

        let createTemplateId (fieldName: string) (raw: string) =
            parseGuid fieldName raw |> Result.map TemplateId

        let createOptionalTemplateId (fieldName: string) (raw: string option) =
            parseOptionalGuid fieldName raw |> Result.map (Option.map TemplateId)

        let createNonEmptyString (fieldName: string) (raw: string option) : Result<NonEmptyString, FieldValidationError> =
            match raw with
            | None -> Error(ApiError.fieldError fieldName ApiErrorCode.EmptyRequiredField)
            | Some s ->
                let trimmed = s.Trim()
                if String.IsNullOrEmpty(trimmed) then
                    Error(ApiError.fieldError fieldName ApiErrorCode.EmptyRequiredField)
                else
                    Ok(NonEmptyString trimmed)

        let createOptionalTrimmedString (raw: string option) : string option =
            match raw with
            | None -> None
            | Some s ->
                let trimmed = s.Trim()
                if String.IsNullOrEmpty(trimmed) then None else Some trimmed

        let createBarcode (fieldName: string) (raw: string option) : Result<Barcode, FieldValidationError> =
            createNonEmptyString fieldName raw
            |> Result.map (fun nes -> Barcode nes.Value)

        let createPositiveQuantity (fieldName: string) (qty: int) : Result<PositiveQuantity, FieldValidationError> =
            if qty <= 0 then
                Error(ApiError.fieldError fieldName ApiErrorCode.InvalidQuantity)
            else
                Ok(PositiveQuantity qty)

        let createPhysicalLocationTarget
            (holderIdRaw: string option)
            (customTextRaw: string option)
            : Result<PhysicalLocationTarget, FieldValidationError> =
            match createOptionalHolderId "to_location_holder_id" holderIdRaw with
            | Error e -> Error e
            | Ok holderOpt ->
                let textOpt = createOptionalTrimmedString customTextRaw
                match holderOpt, textOpt with
                | Some h, Some t -> Ok(AtHolderWithText(h, NonEmptyString t))
                | Some h, None -> Ok(AtHolderOnly h)
                | None, Some t -> Ok(CustomTextOnly(NonEmptyString t))
                | None, None ->
                    Error(ApiError.fieldError "to_location" ApiErrorCode.MissingLocationTarget)
