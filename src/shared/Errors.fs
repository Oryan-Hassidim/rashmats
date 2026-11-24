namespace Rashmatz.Shared

/// Closed machine-readable error codes (zero display strings from server).
[<RequireQualifiedAccess>]
type ApiErrorCode =
    | Unauthenticated | InvalidGoogleToken
    | EmailNotWhitelisted | OrganizationInactive | ForbiddenRole | ForbiddenSuperAdminOnly
    | OrganizationNotFound | HolderNotFound | ItemTypeNotFound | CategoryNotFound
    | SerializedItemNotFound | TemplateNotFound | NoActiveAuditSession
    | OrganizationSlugAlreadyExists | BarcodeAlreadyAssigned | SerialNumberAlreadyAssigned
    | ItemNotOwnedBySourceHolder | InsufficientQuantitativeStock | AuditItemOutOfScope
    | ValidationFailed | InvalidUuidFormat | EmptyRequiredField | InvalidQuantity
    | SameSourceAndDestination | MissingNotesForExternalTransfer | MissingLocationTarget
    | EmptyTransferLines | DuplicateItemInTransfer | HolderInactive | InvalidReturnToPath
    | InternalError

type FieldError = { Field: string; Code: string }

type ApiError =
    | DomainError of code: ApiErrorCode * targetId: string option
    | ValidationErrors of errors: FieldError list

/// Serialized automatically as snake_case (`status`, `code`, `target_id`, `validation_errors`).
type ApiErrorDto =
    { Status: int
      Code: string
      TargetId: string option
      ValidationErrors: FieldError list }

module ApiError =
    let codeString = function
        | ApiErrorCode.Unauthenticated -> "UNAUTHENTICATED"
        | ApiErrorCode.InvalidGoogleToken -> "INVALID_GOOGLE_TOKEN"
        | ApiErrorCode.EmailNotWhitelisted -> "EMAIL_NOT_WHITELISTED"
        | ApiErrorCode.OrganizationInactive -> "ORGANIZATION_INACTIVE"
        | ApiErrorCode.ForbiddenRole -> "FORBIDDEN_ROLE"
        | ApiErrorCode.ForbiddenSuperAdminOnly -> "FORBIDDEN_SUPER_ADMIN_ONLY"
        | ApiErrorCode.OrganizationNotFound -> "ORGANIZATION_NOT_FOUND"
        | ApiErrorCode.HolderNotFound -> "HOLDER_NOT_FOUND"
        | ApiErrorCode.ItemTypeNotFound -> "ITEM_TYPE_NOT_FOUND"
        | ApiErrorCode.CategoryNotFound -> "CATEGORY_NOT_FOUND"
        | ApiErrorCode.SerializedItemNotFound -> "SERIALIZED_ITEM_NOT_FOUND"
        | ApiErrorCode.TemplateNotFound -> "TEMPLATE_NOT_FOUND"
        | ApiErrorCode.NoActiveAuditSession -> "NO_ACTIVE_AUDIT_SESSION"
        | ApiErrorCode.OrganizationSlugAlreadyExists -> "ORGANIZATION_SLUG_ALREADY_EXISTS"
        | ApiErrorCode.BarcodeAlreadyAssigned -> "BARCODE_ALREADY_ASSIGNED"
        | ApiErrorCode.SerialNumberAlreadyAssigned -> "SERIAL_NUMBER_ALREADY_ASSIGNED"
        | ApiErrorCode.ItemNotOwnedBySourceHolder -> "ITEM_NOT_OWNED_BY_SOURCE_HOLDER"
        | ApiErrorCode.InsufficientQuantitativeStock -> "INSUFFICIENT_QUANTITATIVE_STOCK"
        | ApiErrorCode.AuditItemOutOfScope -> "AUDIT_ITEM_OUT_OF_SCOPE"
        | ApiErrorCode.ValidationFailed -> "VALIDATION_FAILED"
        | ApiErrorCode.InvalidUuidFormat -> "INVALID_UUID_FORMAT"
        | ApiErrorCode.EmptyRequiredField -> "EMPTY_REQUIRED_FIELD"
        | ApiErrorCode.InvalidQuantity -> "INVALID_QUANTITY"
        | ApiErrorCode.SameSourceAndDestination -> "SAME_SOURCE_AND_DESTINATION"
        | ApiErrorCode.MissingNotesForExternalTransfer -> "MISSING_NOTES_FOR_EXTERNAL_TRANSFER"
        | ApiErrorCode.MissingLocationTarget -> "MISSING_LOCATION_TARGET"
        | ApiErrorCode.EmptyTransferLines -> "EMPTY_TRANSFER_LINES"
        | ApiErrorCode.DuplicateItemInTransfer -> "DUPLICATE_ITEM_IN_TRANSFER"
        | ApiErrorCode.HolderInactive -> "HOLDER_INACTIVE"
        | ApiErrorCode.InvalidReturnToPath -> "INVALID_RETURN_TO_PATH"
        | ApiErrorCode.InternalError -> "INTERNAL_ERROR"

    let httpStatus = function
        | ApiErrorCode.Unauthenticated | ApiErrorCode.InvalidGoogleToken -> 401
        | ApiErrorCode.EmailNotWhitelisted | ApiErrorCode.OrganizationInactive
        | ApiErrorCode.ForbiddenRole | ApiErrorCode.ForbiddenSuperAdminOnly -> 403
        | ApiErrorCode.OrganizationNotFound | ApiErrorCode.HolderNotFound
        | ApiErrorCode.ItemTypeNotFound | ApiErrorCode.CategoryNotFound
        | ApiErrorCode.SerializedItemNotFound | ApiErrorCode.TemplateNotFound
        | ApiErrorCode.NoActiveAuditSession -> 404
        | ApiErrorCode.OrganizationSlugAlreadyExists | ApiErrorCode.BarcodeAlreadyAssigned
        | ApiErrorCode.SerialNumberAlreadyAssigned | ApiErrorCode.ItemNotOwnedBySourceHolder
        | ApiErrorCode.InsufficientQuantitativeStock | ApiErrorCode.AuditItemOutOfScope -> 409
        | ApiErrorCode.InternalError -> 500
        | _ -> 422

    let single code = DomainError(code, None)
    let withTarget code id = DomainError(code, Some id)
    let field name code = { Field = name; Code = codeString code }
    let fromFields errs = ValidationErrors errs

    let toDto = function
        | DomainError(code, targetId) ->
            { Status = httpStatus code; Code = codeString code; TargetId = targetId; ValidationErrors = [] }
        | ValidationErrors errs ->
            let primary = errs |> List.tryHead |> Option.map _.Code |> Option.defaultValue "VALIDATION_FAILED"
            { Status = 422; Code = primary; TargetId = None; ValidationErrors = errs }
