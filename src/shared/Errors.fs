namespace Rashmatz.Shared

open System.Text.Json.Serialization

/// Closed union of machine-readable error codes.
/// Following Section 1.6 of the specification: zero display text strings are sent from the server.
[<RequireQualifiedAccess>]
type ApiErrorCode =
    // Authentication & Tenancy Errors
    | Unauthenticated
    | EmailNotWhitelisted
    | OrganizationInactive
    | OrganizationNotFound
    | OrganizationSlugAlreadyExists
    | ForbiddenRole
    | ForbiddenSuperAdminOnly
    | InvalidGoogleToken
    | InvalidReturnToPath
    // Domain Validation Errors
    | ValidationFailed
    | InvalidUuidFormat
    | EmptyRequiredField
    | InvalidQuantity
    | SameSourceAndDestination
    | MissingNotesForExternalTransfer
    | MissingLocationTarget
    | EmptyTransferLines
    | DuplicateItemInTransfer
    // Inventory, Relocation & Transfer Errors
    | HolderNotFound
    | HolderInactive
    | ItemTypeNotFound
    | CategoryNotFound
    | SerializedItemNotFound
    | BarcodeAlreadyAssigned
    | SerialNumberAlreadyAssigned
    | ItemNotOwnedBySourceHolder
    | InsufficientQuantitativeStock
    | TemplateNotFound
    | IdempotencyKeyConflict
    // Audit Errors
    | NoActiveAuditSession
    | AuditItemOutOfScope
    // Infrastructure Errors
    | DatabaseConflict
    | InternalError

module ApiErrorCode =
    let toCodeString (code: ApiErrorCode) : string =
        match code with
        | ApiErrorCode.Unauthenticated -> "UNAUTHENTICATED"
        | ApiErrorCode.EmailNotWhitelisted -> "EMAIL_NOT_WHITELISTED"
        | ApiErrorCode.OrganizationInactive -> "ORGANIZATION_INACTIVE"
        | ApiErrorCode.OrganizationNotFound -> "ORGANIZATION_NOT_FOUND"
        | ApiErrorCode.OrganizationSlugAlreadyExists -> "ORGANIZATION_SLUG_ALREADY_EXISTS"
        | ApiErrorCode.ForbiddenRole -> "FORBIDDEN_ROLE"
        | ApiErrorCode.ForbiddenSuperAdminOnly -> "FORBIDDEN_SUPER_ADMIN_ONLY"
        | ApiErrorCode.InvalidGoogleToken -> "INVALID_GOOGLE_TOKEN"
        | ApiErrorCode.InvalidReturnToPath -> "INVALID_RETURN_TO_PATH"
        | ApiErrorCode.ValidationFailed -> "VALIDATION_FAILED"
        | ApiErrorCode.InvalidUuidFormat -> "INVALID_UUID_FORMAT"
        | ApiErrorCode.EmptyRequiredField -> "EMPTY_REQUIRED_FIELD"
        | ApiErrorCode.InvalidQuantity -> "INVALID_QUANTITY"
        | ApiErrorCode.SameSourceAndDestination -> "SAME_SOURCE_AND_DESTINATION"
        | ApiErrorCode.MissingNotesForExternalTransfer -> "MISSING_NOTES_FOR_EXTERNAL_TRANSFER"
        | ApiErrorCode.MissingLocationTarget -> "MISSING_LOCATION_TARGET"
        | ApiErrorCode.EmptyTransferLines -> "EMPTY_TRANSFER_LINES"
        | ApiErrorCode.DuplicateItemInTransfer -> "DUPLICATE_ITEM_IN_TRANSFER"
        | ApiErrorCode.HolderNotFound -> "HOLDER_NOT_FOUND"
        | ApiErrorCode.HolderInactive -> "HOLDER_INACTIVE"
        | ApiErrorCode.ItemTypeNotFound -> "ITEM_TYPE_NOT_FOUND"
        | ApiErrorCode.CategoryNotFound -> "CATEGORY_NOT_FOUND"
        | ApiErrorCode.SerializedItemNotFound -> "SERIALIZED_ITEM_NOT_FOUND"
        | ApiErrorCode.BarcodeAlreadyAssigned -> "BARCODE_ALREADY_ASSIGNED"
        | ApiErrorCode.SerialNumberAlreadyAssigned -> "SERIAL_NUMBER_ALREADY_ASSIGNED"
        | ApiErrorCode.ItemNotOwnedBySourceHolder -> "ITEM_NOT_OWNED_BY_SOURCE_HOLDER"
        | ApiErrorCode.InsufficientQuantitativeStock -> "INSUFFICIENT_QUANTITATIVE_STOCK"
        | ApiErrorCode.TemplateNotFound -> "TEMPLATE_NOT_FOUND"
        | ApiErrorCode.IdempotencyKeyConflict -> "IDEMPOTENCY_KEY_CONFLICT"
        | ApiErrorCode.NoActiveAuditSession -> "NO_ACTIVE_AUDIT_SESSION"
        | ApiErrorCode.AuditItemOutOfScope -> "AUDIT_ITEM_OUT_OF_SCOPE"
        | ApiErrorCode.DatabaseConflict -> "DATABASE_CONFLICT"
        | ApiErrorCode.InternalError -> "INTERNAL_ERROR"

    let toHttpStatus (code: ApiErrorCode) : int =
        match code with
        | ApiErrorCode.Unauthenticated
        | ApiErrorCode.InvalidGoogleToken -> 401
        | ApiErrorCode.EmailNotWhitelisted
        | ApiErrorCode.OrganizationInactive
        | ApiErrorCode.ForbiddenRole
        | ApiErrorCode.ForbiddenSuperAdminOnly -> 403
        | ApiErrorCode.OrganizationNotFound
        | ApiErrorCode.HolderNotFound
        | ApiErrorCode.ItemTypeNotFound
        | ApiErrorCode.CategoryNotFound
        | ApiErrorCode.SerializedItemNotFound
        | ApiErrorCode.TemplateNotFound
        | ApiErrorCode.NoActiveAuditSession -> 404
        | ApiErrorCode.OrganizationSlugAlreadyExists
        | ApiErrorCode.BarcodeAlreadyAssigned
        | ApiErrorCode.SerialNumberAlreadyAssigned
        | ApiErrorCode.ItemNotOwnedBySourceHolder
        | ApiErrorCode.InsufficientQuantitativeStock
        | ApiErrorCode.IdempotencyKeyConflict
        | ApiErrorCode.AuditItemOutOfScope
        | ApiErrorCode.DatabaseConflict -> 409
        | ApiErrorCode.ValidationFailed
        | ApiErrorCode.InvalidUuidFormat
        | ApiErrorCode.EmptyRequiredField
        | ApiErrorCode.InvalidQuantity
        | ApiErrorCode.SameSourceAndDestination
        | ApiErrorCode.MissingNotesForExternalTransfer
        | ApiErrorCode.MissingLocationTarget
        | ApiErrorCode.EmptyTransferLines
        | ApiErrorCode.DuplicateItemInTransfer
        | ApiErrorCode.HolderInactive
        | ApiErrorCode.InvalidReturnToPath -> 422
        | ApiErrorCode.InternalError -> 500

/// Represents a field-level validation failure without human-facing display text.
type FieldValidationError =
    { [<JsonPropertyName("field")>]
      Field: string
      [<JsonPropertyName("code")>]
      Code: string }

/// Domain / Railway error carried through Result<'T, ApiError> and TaskResult<'T, ApiError>.
type ApiError =
    | DomainError of code: ApiErrorCode * targetId: string option
    | ValidationErrors of errors: FieldValidationError list

/// Wire format returned to the client httpClient on non-2xx responses.
/// Matches client_architecture_code_standards.md Section 6.1 (with machine code, no display prose).
type ApiErrorResponseDto =
    { [<JsonPropertyName("status")>]
      Status: int
      [<JsonPropertyName("code")>]
      Code: string
      [<JsonPropertyName("target_id")>]
      TargetId: string option
      [<JsonPropertyName("validation_errors")>]
      ValidationErrors: FieldValidationError list }

module ApiError =
    let single (code: ApiErrorCode) : ApiError =
        DomainError(code, None)

    let withTarget (code: ApiErrorCode) (targetId: string) : ApiError =
        DomainError(code, Some targetId)

    let fieldError (field: string) (code: ApiErrorCode) : FieldValidationError =
        { Field = field
          Code = ApiErrorCode.toCodeString code }

    let fromFieldErrors (errors: FieldValidationError list) : ApiError =
        ValidationErrors errors

    let toDto (err: ApiError) : ApiErrorResponseDto =
        match err with
        | DomainError(code, targetId) ->
            { Status = ApiErrorCode.toHttpStatus code
              Code = ApiErrorCode.toCodeString code
              TargetId = targetId
              ValidationErrors = [] }
        | ValidationErrors errs ->
            let primaryCode =
                match errs with
                | first :: _ -> first.Code
                | [] -> ApiErrorCode.toCodeString ApiErrorCode.ValidationFailed

            { Status = 422
              Code = primaryCode
              TargetId = None
              ValidationErrors = errs }
