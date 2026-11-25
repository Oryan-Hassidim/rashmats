namespace Rashmatz.Pages.TransfersNewItems

open System.ComponentModel.DataAnnotations
open FsToolkit.ErrorHandling
open Rashmatz.Shared

/// Step 6C Domain — Clean DTOs (auto snake_case), Scott Wlaschin sum types, and applicative `validation { ... }`.
module Domain =

    // ── Read Models & Wire DTOs ──────────────────────────────────────────────
    type TemplateLine = { LineNumber: int; ItemTypeId: string; ItemTypeName: string; TrackingType: int; Quantity: int }
    type SelectedTemplate = { Id: string; Name: string; Description: string option; DefaultSourceHolderId: string option; DefaultDestHolderId: string option; Lines: TemplateLine list }
    type CatalogItemType = { Id: string; CategoryId: string option; CategoryName: string option; Name: string; TrackingType: int; UnitOfMeasure: string; Description: string option }
    type SourceSerializedItem = { Id: string; ItemTypeId: string; ItemTypeName: string; CategoryId: string option; UnitOfMeasure: string; Barcode: string; SerialNumber: string option; Status: int; LocationHolderId: string option; LocationCustomText: string option }
    type SourceQuantBalance = { ItemTypeId: string; ItemTypeName: string; CategoryId: string option; UnitOfMeasure: string; Quantity: int }
    type DestBalance = { ItemTypeId: string; TrackingType: int; CurrentQuantity: int }

    type TransfersNewItemsPage =
        { SourceHolderId: string option; DestHolderId: string option
          Categories: Rashmatz.Pages.MyCustody.Server.CategorySummary list
          SourceSerializedItems: SourceSerializedItem list
          SourceQuantitativeBalances: SourceQuantBalance list
          DestBalances: DestBalance list
          SelectedTemplate: SelectedTemplate option
          IntakeCatalogItemTypes: CatalogItemType list option }

    [<CLIMutable>] type CheckUnassignedQuery = { Barcode: string option; SerialNumber: string option }
    type CheckUnassignedResponse = { BarcodeAvailable: bool; SerialNumberAvailable: bool; ConflictingItemId: string option }

    [<CLIMutable>]
    type CreateItemTypeRequest =
        { [<Required; MinLength(1)>] Name: string
          CategoryId: string option
          NewCategoryName: string option
          [<Range(0, 1)>] TrackingType: int option
          UnitOfMeasure: string option
          Description: string option }

    [<CLIMutable>]
    type IntakeSerializedInput =
        { [<Required; MinLength(1)>] ItemTypeId: string
          [<Required; MinLength(1)>] Barcode: string
          SerialNumber: string option }

    [<CLIMutable>]
    type QuantLineInput =
        { [<Required; MinLength(1)>] ItemTypeId: string
          [<Range(1, 1000000)>] Quantity: int }

    [<CLIMutable>]
    type CommitTransferRequest =
        { SourceHolderId: string option; DestHolderId: string option; TransferTemplateId: string option
          SerializedItemIds: string list option; NewIntakeSerializedItems: IntakeSerializedInput list option
          QuantitativeLines: QuantLineInput list option
          UpdatePhysicalLocation: bool option; ToLocationHolderId: string option; ToLocationCustomText: string option
          Notes: string option }

    type CommitTransferResponse = { TransferId: string; TransferNumber: string; TransferredAt: string; LinesCount: int; PhysicalLocationUpdated: bool }

    // ── Domain Commands (Illegal States Unrepresentable) ─────────────────────
    type CategoryChoice = ExistingCategory of CategoryId | NewCategory of NonEmptyText | Uncategorized
    type ValidatedCreateItemType = { Name: NonEmptyText; Category: CategoryChoice; TrackingType: TrackingType; UnitOfMeasure: string; Description: string option }

    type TransferParties =
        | Internal of source: HolderId * dest: HolderId * notes: string option
        | ExternalIntake of dest: HolderId * notes: NonEmptyText
        | ExternalDisposal of source: HolderId * notes: NonEmptyText

        member x.SourceOpt = match x with Internal(s, _, _) | ExternalDisposal(s, _) -> Some s | ExternalIntake _ -> None
        member x.DestOpt = match x with Internal(_, d, _) | ExternalIntake(d, _) -> Some d | ExternalDisposal _ -> None
        member x.NotesOpt = match x with Internal(_, _, n) -> n | ExternalIntake(_, n) | ExternalDisposal(_, n) -> Some n.Value

    type LocationSync = KeepLocation | SyncTo of PhysicalLocation
    type IntakeSpec = { ItemTypeId: ItemTypeId; Barcode: Barcode; SerialNumber: string option }
    type QuantSpec = { ItemTypeId: ItemTypeId; Quantity: PositiveQty }

    type ValidatedTransfer =
        { Parties: TransferParties; TemplateId: TemplateId option
          ExistingSerialized: SerializedItemId list; IntakeSerialized: IntakeSpec list
          QuantLines: QuantSpec list; LocationSync: LocationSync }

    // ── Applicative Validation ───────────────────────────────────────────────
    let validateCreateItemType (req: CreateItemTypeRequest) : Result<ValidatedCreateItemType, ApiError> =
        validation {
            let! name = Parse.nonEmpty "name" (Some req.Name)
            and! catGuid = Parse.optGuid "category_id" req.CategoryId
            and! tracking =
                match req.TrackingType |> Option.defaultValue 1 with
                | 0 -> Ok TrackingType.Quantitative
                | 1 -> Ok TrackingType.Serialized
                | _ -> Error(ApiError.field "tracking_type" ApiErrorCode.ValidationFailed)

            let category =
                match catGuid, Parse.nonEmpty "new_category_name" req.NewCategoryName with
                | Some g, _ -> ExistingCategory(CategoryId g)
                | None, Ok n -> NewCategory n
                | None, Error _ -> Uncategorized

            return
                { Name = name; Category = category; TrackingType = tracking
                  UnitOfMeasure = trimOpt req.UnitOfMeasure |> Option.defaultValue "unit"
                  Description = trimOpt req.Description }
        }
        |> Result.mapError ApiError.fromFields

    let private validateParties srcRaw dstRaw notesRaw =
        validation {
            let! src = Parse.optHolderId "source_holder_id" srcRaw
            and! dst = Parse.optHolderId "dest_holder_id" dstRaw
            return src, dst
        }
        |> Result.bind (fun (src, dst) ->
            match src, dst with
            | Some s, Some d when s = d -> Error [ ApiError.field "dest_holder_id" ApiErrorCode.SameSourceAndDestination ]
            | Some s, Some d -> Ok(Internal(s, d, trimOpt notesRaw))
            | None, Some d ->
                Parse.nonEmpty "notes" notesRaw
                |> Result.map (fun n -> ExternalIntake(d, n))
                |> Result.mapError (fun _ -> [ ApiError.field "notes" ApiErrorCode.MissingNotesForExternalTransfer ])
            | Some s, None ->
                Parse.nonEmpty "notes" notesRaw
                |> Result.map (fun n -> ExternalDisposal(s, n))
                |> Result.mapError (fun _ -> [ ApiError.field "notes" ApiErrorCode.MissingNotesForExternalTransfer ])
            | None, None -> Error [ ApiError.field "parties" ApiErrorCode.EmptyRequiredField ])

    let private validateDistinctList (field: string) (keyOf: 'a -> 'k) (results: Result<'a, FieldError list> list) =
        let errs = results |> List.collect (function Error e -> e | Ok _ -> [])
        if not errs.IsEmpty then Error errs
        else
            let items = results |> List.choose (function Ok v -> Some v | Error _ -> None)
            if (items |> List.map keyOf |> List.distinct |> List.length) <> items.Length then
                Error [ ApiError.field field ApiErrorCode.DuplicateItemInTransfer ]
            else Ok items

    let private resolveLocationSync (parties: TransferParties) (req: CommitTransferRequest) (explicitHolder: HolderId option) =
        if not (req.UpdatePhysicalLocation |> Option.defaultValue true) then KeepLocation
        else
            let holder = explicitHolder |> Option.orElse parties.DestOpt
            match holder, Parse.nonEmpty "to_location_custom_text" req.ToLocationCustomText with
            | Some h, Ok t -> SyncTo(AtHolderAndText(h, t))
            | Some h, Error _ -> SyncTo(AtHolder h)
            | None, Ok t -> SyncTo(CustomText t)
            | None, Error _ -> KeepLocation

    let validateCommitTransfer (req: CommitTransferRequest) : Result<ValidatedTransfer, ApiError> =
        let serResults =
            (req.SerializedItemIds |> Option.defaultValue [])
            |> List.mapi (fun i id -> Parse.serializedItemId $"serialized_item_ids[{i}]" id |> Result.mapError List.singleton)

        let intakeResults =
            (req.NewIntakeSerializedItems |> Option.defaultValue [])
            |> List.mapi (fun i x ->
                validation {
                    let! tid = Parse.itemTypeId $"new_intake_serialized_items[{i}].item_type_id" (Some x.ItemTypeId |> Option.defaultValue "")
                    and! bc = Parse.barcode $"new_intake_serialized_items[{i}].barcode" (Some x.Barcode)
                    return { ItemTypeId = tid; Barcode = bc; SerialNumber = trimOpt x.SerialNumber }
                })

        let quantResults =
            (req.QuantitativeLines |> Option.defaultValue [])
            |> List.mapi (fun i x ->
                validation {
                    let! tid = Parse.itemTypeId $"quantitative_lines[{i}].item_type_id" (Some x.ItemTypeId |> Option.defaultValue "")
                    and! qty = Parse.positiveQty $"quantitative_lines[{i}].quantity" x.Quantity
                    return { ItemTypeId = tid; Quantity = qty }
                })

        validation {
            let! parties = validateParties req.SourceHolderId req.DestHolderId req.Notes
            and! tplId = Parse.optTemplateId "transfer_template_id" req.TransferTemplateId |> Result.mapError List.singleton
            and! existingSer = validateDistinctList "serialized_item_ids" id serResults
            and! intakeSer = validateDistinctList "new_intake_serialized_items" (fun x -> x.Barcode.Value.ToLowerInvariant()) intakeResults
            and! quantLines = validateDistinctList "quantitative_lines" _.ItemTypeId quantResults
            and! locHolder = Parse.optHolderId "to_location_holder_id" req.ToLocationHolderId |> Result.mapError List.singleton
            return parties, tplId, existingSer, intakeSer, quantLines, locHolder
        }
        |> Result.bind (fun (parties, tplId, existingSer, intakeSer, quantLines, locHolder) ->
            if existingSer.Length + intakeSer.Length + quantLines.Length = 0 then
                Error [ ApiError.field "lines" ApiErrorCode.EmptyTransferLines ]
            else
                match parties with
                | ExternalIntake _ when not existingSer.IsEmpty -> Error [ ApiError.field "serialized_item_ids" ApiErrorCode.ValidationFailed ]
                | (Internal _ | ExternalDisposal _) when not intakeSer.IsEmpty -> Error [ ApiError.field "new_intake_serialized_items" ApiErrorCode.ValidationFailed ]
                | _ ->
                    Ok { Parties = parties; TemplateId = tplId; ExistingSerialized = existingSer
                         IntakeSerialized = intakeSer; QuantLines = quantLines
                         LocationSync = resolveLocationSync parties req locHolder })
        |> Result.mapError ApiError.fromFields
