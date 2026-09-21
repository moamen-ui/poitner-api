namespace Pointer.Domain.Enums;

/// <summary>
/// Input type of an admin-defined comment field (R4-01). Drives widget rendering, write-side
/// validation and the resolved read DTO. Numeric values are persisted in definitions and sent
/// on the wire — do not renumber.
/// </summary>
public enum CommentFieldType { Text = 1, Url = 2, Select = 3 }
