namespace Rashmatz.Pages.TransfersNewItems

open System
open System.Text.Json.Serialization
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes

/// Step 6C Domain Module (`TransfersNewItems.Domain.fs`):
/// Pure functional domain modeling following Scott Wlaschin's "Domain Modeling Made Functional".
/// Makes illegal transfer states unrepresentable at the type level:
/// - External Intake (`source = None`) and Disposal (`dest = None`) require `NonEmptyString` notes.
/// - Internal Transfer (`source = Some`, `dest = Some`) guarantees `source <> dest`.
/// - Uses `FsToolkit.ErrorHandling` `validation { ... }` to collect all multi-field errors simultaneously.
module Domain =

    // =========================================================================
    // 1. Read Models / Bootstrap DTOs for Page 6C
    // =========================================================================

    type TemplateLineDto =
        { [<JsonPropertyName("line_number")>]
          LineNumber: int
          [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("tracking_type")>]
          TrackingType: int
          [<JsonPropertyName("quantity")>]
          Quantity: int }

    type SelectedTemplateWithLinesDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("description")>]
          Description: string option
          [<JsonPropertyName("default_source_holder_id")>]
          DefaultSourceHolderId: string option
          [<JsonPropertyName("default_dest_holder_id")>]
          DefaultDestHolderId: string option
          [<JsonPropertyName("lines")>]
          Lines: TemplateLineDto list }

    type CatalogItemTypeDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("category_name")>]
          CategoryName: string option
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("tracking_type")>]
          TrackingType: int
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string
          [<JsonPropertyName("description")>]
          Description: string option }

    type HolderSerializedItemDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string
          [<JsonPropertyName("barcode")>]
          Barcode: string
          [<JsonPropertyName("serial_number")>]
          SerialNumber: string option
          [<JsonPropertyName("status")>]
          Status: int
          [<JsonPropertyName("location_holder_id")>]
          LocationHolderId: string option
          [<JsonPropertyName("location_custom_text")>]
          LocationCustomText: string option }

    type HolderQuantitativeBalanceDto =
        { [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string
          [<JsonPropertyName("quantity")>]
          Quantity: int }

    type DestinationItemBalanceDto =
        { [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("tracking_type")>]
          TrackingType: int
          [<JsonPropertyName("current_quantity")>]
          CurrentQuantity: int }

    type TransfersNewItemsPageDto =
        { [<JsonPropertyName("source_holder_id")>]
          SourceHolderId: string option
          [<JsonPropertyName("dest_holder_id")>]
          DestHolderId: string option
          [<JsonPropertyName("categories")>]
          Categories: Rashmatz.Pages.MyCustody.Server.CategorySummaryDto list
          [<JsonPropertyName("source_serialized_items")>]
          SourceSerializedItems: HolderSerializedItemDto list
          [<JsonPropertyName("source_quantitative_balances")>]
          SourceQuantitativeBalances: HolderQuantitativeBalanceDto list
          [<JsonPropertyName("dest_balances")>]
          DestBalances: DestinationItemBalanceDto list
          [<JsonPropertyName("selected_template")>]
          SelectedTemplate: SelectedTemplateWithLinesDto option
          [<JsonPropertyName("intake_catalog_item_types")>]
          IntakeCatalogItemTypes: CatalogItemTypeDto list option }

    // =========================================================================
    // 2. Wire Request / Response DTOs for Page 6C Commands & Queries
    // =========================================================================

    [<CLIMutable>]
    type CheckUnassignedItemQueryDto =
        { [<JsonPropertyName("barcode")>]
          Barcode: string option
          [<JsonPropertyName("serial_number")>]
          SerialNumber: string option }

    type CheckUnassignedItemResponseDto =
        { [<JsonPropertyName("barcode_available")>]
          BarcodeAvailable: bool
          [<JsonPropertyName("serial_number_available")>]
          SerialNumberAvailable: bool
          [<JsonPropertyName("conflicting_item_id")>]
          ConflictingItemId: string option }

    [<CLIMutable>]
    type CreateItemTypeRequestDto =
        { [<JsonPropertyName("name")>]
          Name: string option
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("new_category_name")>]
          NewCategoryName: string option
          [<JsonPropertyName("tracking_type")>]
          TrackingType: int option
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string option
          [<JsonPropertyName("description")>]
          Description: string option }

    [<CLIMutable>]
    type TransferSerializedIntakeItemDto =
        { [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string option
          [<JsonPropertyName("barcode")>]
          Barcode: string option
          [<JsonPropertyName("serial_number")>]
          SerialNumber: string option }

    [<CLIMutable>]
    type TransferQuantitativeLineDto =
        { [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string option
          [<JsonPropertyName("quantity")>]
          Quantity: int }

    [<CLIMutable>]
    type CommitTransferRequestDto =
        { [<JsonPropertyName("source_holder_id")>]
          SourceHolderId: string option
          [<JsonPropertyName("dest_holder_id")>]
          DestHolderId: string option
          [<JsonPropertyName("transfer_template_id")>]
          TransferTemplateId: string option
          [<JsonPropertyName("serialized_item_ids")>]
          SerializedItemIds: string list option
          [<JsonPropertyName("new_intake_serialized_items")>]
          NewIntakeSerializedItems: TransferSerializedIntakeItemDto list option
          [<JsonPropertyName("quantitative_lines")>]
          QuantitativeLines: TransferQuantitativeLineDto list option
          [<JsonPropertyName("update_physical_location")>]
          UpdatePhysicalLocation: bool option
          [<JsonPropertyName("to_location_holder_id")>]
          ToLocationHolderId: string option
          [<JsonPropertyName("to_location_custom_text")>]
          ToLocationCustomText: string option
          [<JsonPropertyName("notes")>]
          Notes: string option }

    type CommitTransferResponseDto =
        { [<JsonPropertyName("transfer_id")>]
          TransferId: string
          [<JsonPropertyName("transfer_number")>]
          TransferNumber: string
          [<JsonPropertyName("transferred_at")>]
          TransferredAt: string
          [<JsonPropertyName("lines_count")>]
          LinesCount: int
          [<JsonPropertyName("physical_location_updated")>]
          PhysicalLocationUpdated: bool }

    // =========================================================================
    // 3. Pure Domain Types (Making Illegal States Unrepresentable)
    // =========================================================================

    type CategoryAssignment =
        | ExistingCategory of CategoryId
        | CreateNewCategory of NonEmptyString
        | Uncategorized

    type ValidatedCreateItemTypeCommand =
        { Name: NonEmptyString
          Category: CategoryAssignment
          TrackingType: TrackingType
          UnitOfMeasure: string
          Description: string option }

    /// Enforces db model.md Section 7:
    /// - InternalTransfer: both source and dest exist, and source <> dest.
    /// - ExternalIntake: source is None, dest exists, and NonEmptyString notes are strictly mandatory.
    /// - ExternalDisposal: source exists, dest is None, and NonEmptyString notes are strictly mandatory.
    type TransferDirectionAndParties =
        | InternalTransfer of source: HolderId * dest: HolderId * notes: string option
        | ExternalIntake of dest: HolderId * mandatoryNotes: NonEmptyString
        | ExternalDisposal of source: HolderId * mandatoryNotes: NonEmptyString

        member this.SourceHolderIdOpt =
            match this with
            | InternalTransfer(s, _, _) -> Some s
            | ExternalIntake _ -> None
            | ExternalDisposal(s, _) -> Some s

        member this.DestHolderIdOpt =
            match this with
            | InternalTransfer(_, d, _) -> Some d
            | ExternalIntake(d, _) -> Some d
            | ExternalDisposal _ -> None

        member this.NotesOpt =
            match this with
            | InternalTransfer(_, _, n) -> n
            | ExternalIntake(_, n) -> Some n.Value
            | ExternalDisposal(_, n) -> Some n.Value

    /// Models the Physical Location Sync Option on Page 6C (Section 3, Page 6C.4).
    type PhysicalLocationSync =
        | KeepExistingLocation
        | SyncToLocation of target: PhysicalLocationTarget

    type ValidatedIntakeSerializedSpec =
        { ItemTypeId: ItemTypeId
          Barcode: Barcode
          SerialNumber: string option }

    type ValidatedQuantitativeTransferSpec =
        { ItemTypeId: ItemTypeId
          Quantity: PositiveQuantity }

    type ValidatedCommitTransferCommand =
        { Parties: TransferDirectionAndParties
          TemplateId: TemplateId option
          ExistingSerializedItemIds: SerializedItemId list
          NewIntakeSerializedSpecs: ValidatedIntakeSerializedSpec list
          QuantitativeLines: ValidatedQuantitativeTransferSpec list
          LocationSync: PhysicalLocationSync }

    // =========================================================================
    // 4. Multi-Field Applicative Validation (`validation { ... }`)
    // =========================================================================

    let validateCreateItemType (req: CreateItemTypeRequestDto) : Result<ValidatedCreateItemTypeCommand, ApiError> =
        validation {
            let! name = SmartConstructors.createNonEmptyString "name" req.Name
            and! existingCatOpt = SmartConstructors.createOptionalGuid "category_id" req.CategoryId
            and! trackingType =
                match req.TrackingType |> Option.defaultValue 1 with
                | 0 -> Ok TrackingType.Quantitative
                | 1 -> Ok TrackingType.Serialized
                | _ -> Error(ApiError.fieldError "tracking_type" ApiErrorCode.ValidationFailed)

            let categoryAssignment =
                match existingCatOpt, SmartConstructors.createOptionalTrimmedString req.NewCategoryName with
                | Some catGuid, _ -> ExistingCategory(CategoryId catGuid)
                | None, Some newCatName ->
                    match SmartConstructors.createNonEmptyString "new_category_name" (Some newCatName) with
                    | Ok nes -> CreateNewCategory nes
                    | Error _ -> Uncategorized
                | None, None -> Uncategorized

            let unitOfMeasure =
                SmartConstructors.createOptionalTrimmedString req.UnitOfMeasure
                |> Option.defaultValue "unit"

            let description = SmartConstructors.createOptionalTrimmedString req.Description

            return
                { Name = name
                  Category = categoryAssignment
                  TrackingType = trackingType
                  UnitOfMeasure = unitOfMeasure
                  Description = description }
        }
        |> Result.mapError ApiError.fromFieldErrors

    let private validatePartiesAndNotes
        (sourceRaw: string option)
        (destRaw: string option)
        (notesRaw: string option)
        : Result<TransferDirectionAndParties, FieldValidationError list> =
        validation {
            let! sourceOpt = SmartConstructors.createOptionalHolderId "source_holder_id" sourceRaw
            and! destOpt = SmartConstructors.createOptionalHolderId "dest_holder_id" destRaw
            return sourceOpt, destOpt
        }
        |> Result.bind (fun (sourceOpt, destOpt) ->
            let trimmedNotesOpt = SmartConstructors.createOptionalTrimmedString notesRaw

            match sourceOpt, destOpt with
            | Some src, Some dst ->
                if src.Value = dst.Value then
                    Error [ ApiError.fieldError "dest_holder_id" ApiErrorCode.SameSourceAndDestination ]
                else
                    Ok(InternalTransfer(src, dst, trimmedNotesOpt))
            | None, Some dst ->
                match SmartConstructors.createNonEmptyString "notes" notesRaw with
                | Ok mandatoryNotes -> Ok(ExternalIntake(dst, mandatoryNotes))
                | Error _ -> Error [ ApiError.fieldError "notes" ApiErrorCode.MissingNotesForExternalTransfer ]
            | Some src, None ->
                match SmartConstructors.createNonEmptyString "notes" notesRaw with
                | Ok mandatoryNotes -> Ok(ExternalDisposal(src, mandatoryNotes))
                | Error _ -> Error [ ApiError.fieldError "notes" ApiErrorCode.MissingNotesForExternalTransfer ]
            | None, None ->
                Error [ ApiError.fieldError "parties" ApiErrorCode.EmptyRequiredField ])

    let private validateExistingSerializedIds
        (rawIds: string list)
        : Result<SerializedItemId list, FieldValidationError list> =
        let parsedResults =
            rawIds
            |> List.mapi (fun idx raw ->
                SmartConstructors.createSerializedItemId $"serialized_item_ids[{idx}]" raw)

        let errors =
            parsedResults
            |> List.choose (function
                | Error e -> Some e
                | Ok _ -> None)

        if not (List.isEmpty errors) then
            Error errors
        else
            let validIds =
                parsedResults
                |> List.choose (function
                    | Ok v -> Some v
                    | Error _ -> None)

            let distinctCount = validIds |> List.map (fun x -> x.Value) |> List.distinct |> List.length

            if distinctCount <> validIds.Length then
                Error [ ApiError.fieldError "serialized_item_ids" ApiErrorCode.DuplicateItemInTransfer ]
            else
                Ok validIds

    let private validateIntakeSpecs
        (rawItems: TransferSerializedIntakeItemDto list)
        : Result<ValidatedIntakeSerializedSpec list, FieldValidationError list> =
        let parsedResults =
            rawItems
            |> List.mapi (fun idx item ->
                validation {
                    let! itemTypeId =
                        SmartConstructors.createItemTypeId
                            $"new_intake_serialized_items[{idx}].item_type_id"
                            (Option.defaultValue "" item.ItemTypeId)
                    and! barcode =
                        SmartConstructors.createBarcode
                            $"new_intake_serialized_items[{idx}].barcode"
                            item.Barcode

                    let serialOpt = SmartConstructors.createOptionalTrimmedString item.SerialNumber

                    return
                        { ItemTypeId = itemTypeId
                          Barcode = barcode
                          SerialNumber = serialOpt }
                })

        let errors =
            parsedResults
            |> List.collect (function
                | Error errs -> errs
                | Ok _ -> [])

        if not (List.isEmpty errors) then
            Error errors
        else
            let specs =
                parsedResults
                |> List.choose (function
                    | Ok s -> Some s
                    | Error _ -> None)

            let distinctBarcodes =
                specs
                |> List.map (fun s -> s.Barcode.Value.ToLowerInvariant())
                |> List.distinct
                |> List.length

            if distinctBarcodes <> specs.Length then
                Error [ ApiError.fieldError "new_intake_serialized_items" ApiErrorCode.DuplicateItemInTransfer ]
            else
                Ok specs

    let private validateQuantitativeSpecs
        (rawLines: TransferQuantitativeLineDto list)
        : Result<ValidatedQuantitativeTransferSpec list, FieldValidationError list> =
        let parsedResults =
            rawLines
            |> List.mapi (fun idx line ->
                validation {
                    let! itemTypeId =
                        SmartConstructors.createItemTypeId
                            $"quantitative_lines[{idx}].item_type_id"
                            (Option.defaultValue "" line.ItemTypeId)
                    and! qty =
                        SmartConstructors.createPositiveQuantity
                            $"quantitative_lines[{idx}].quantity"
                            line.Quantity

                    return
                        { ItemTypeId = itemTypeId
                          Quantity = qty }
                })

        let errors =
            parsedResults
            |> List.collect (function
                | Error errs -> errs
                | Ok _ -> [])

        if not (List.isEmpty errors) then
            Error errors
        else
            let specs =
                parsedResults
                |> List.choose (function
                    | Ok s -> Some s
                    | Error _ -> None)

            let distinctItemTypes =
                specs
                |> List.map (fun s -> s.ItemTypeId.Value)
                |> List.distinct
                |> List.length

            if distinctItemTypes <> specs.Length then
                Error [ ApiError.fieldError "quantitative_lines" ApiErrorCode.DuplicateItemInTransfer ]
            else
                Ok specs

    let validateCommitTransfer (req: CommitTransferRequestDto) : Result<ValidatedCommitTransferCommand, ApiError> =
        let rawSerializedIds = Option.defaultValue [] req.SerializedItemIds
        let rawIntakeItems = Option.defaultValue [] req.NewIntakeSerializedItems
        let rawQuantLines = Option.defaultValue [] req.QuantitativeLines

        validation {
            let! parties = validatePartiesAndNotes req.SourceHolderId req.DestHolderId req.Notes
            and! templateIdOpt =
                SmartConstructors.createOptionalTemplateId "transfer_template_id" req.TransferTemplateId
                |> Result.mapError List.singleton
            and! existingSerializedIds = validateExistingSerializedIds rawSerializedIds
            and! intakeSpecs = validateIntakeSpecs rawIntakeItems
            and! quantSpecs = validateQuantitativeSpecs rawQuantLines
            and! explicitLocHolderOpt =
                SmartConstructors.createOptionalHolderId "to_location_holder_id" req.ToLocationHolderId
                |> Result.mapError List.singleton

            return (parties, templateIdOpt, existingSerializedIds, intakeSpecs, quantSpecs, explicitLocHolderOpt)
        }
        |> Result.bind (fun (parties, templateIdOpt, existingSerializedIds, intakeSpecs, quantSpecs, explicitLocHolderOpt) ->
            let totalLines =
                existingSerializedIds.Length + intakeSpecs.Length + quantSpecs.Length

            if totalLines = 0 then
                Error [ ApiError.fieldError "lines" ApiErrorCode.EmptyTransferLines ]
            else
                // Only ExternalIntake can create brand-new serialized items; Internal/Disposal must reference existing IDs
                match parties with
                | InternalTransfer _
                | ExternalDisposal _ when not (List.isEmpty intakeSpecs) ->
                    Error [ ApiError.fieldError "new_intake_serialized_items" ApiErrorCode.ValidationFailed ]
                | ExternalIntake _ when not (List.isEmpty existingSerializedIds) ->
                    Error [ ApiError.fieldError "serialized_item_ids" ApiErrorCode.ValidationFailed ]
                | _ ->
                    // Resolve PhysicalLocationSync policy (default is update_physical_location = true)
                    let updateLoc = Option.defaultValue true req.UpdatePhysicalLocation
                    let customTextOpt = SmartConstructors.createOptionalTrimmedString req.ToLocationCustomText

                    let locationSync =
                        if not updateLoc then
                            KeepExistingLocation
                        else
                            let effectiveHolderOpt =
                                match explicitLocHolderOpt with
                                | Some h -> Some h
                                | None -> parties.DestHolderIdOpt

                            match effectiveHolderOpt, customTextOpt with
                            | Some h, Some t ->
                                match SmartConstructors.createNonEmptyString "to_location_custom_text" (Some t) with
                                | Ok nes -> SyncToLocation(AtHolderWithText(h, nes))
                                | Error _ -> SyncToLocation(AtHolderOnly h)
                            | Some h, None -> SyncToLocation(AtHolderOnly h)
                            | None, Some t ->
                                match SmartConstructors.createNonEmptyString "to_location_custom_text" (Some t) with
                                | Ok nes -> SyncToLocation(CustomTextOnly nes)
                                | Error _ -> KeepExistingLocation
                            | None, None -> KeepExistingLocation

                    Ok
                        { Parties = parties
                          TemplateId = templateIdOpt
                          ExistingSerializedItemIds = existingSerializedIds
                          NewIntakeSerializedSpecs = intakeSpecs
                          QuantitativeLines = quantSpecs
                          LocationSync = locationSync })
        |> Result.mapError ApiError.fromFieldErrors
