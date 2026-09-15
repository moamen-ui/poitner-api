namespace Pointer.Domain.Enums;

/// <summary>
/// Which deployment a comment came from.
///
/// <c>Unknown = 0</c> is deliberately the default, and carries two meanings that are the same thing
/// from the reader's point of view: the widget did not state an environment (so the server resolved
/// it from the request's Origin), and that origin matched no URL registered against the project.
///
/// Guessing instead — falling back to Local, or to the project's first active environment — is what
/// this value exists to avoid. A URL nobody registered would then quietly file real staging or
/// production feedback under the wrong label, and nothing in the data would ever reveal it. Tagged
/// Unknown, the comment is still captured, still answerable, and visibly asks its owner to register
/// the origin.
/// </summary>
public enum EnvironmentTag { Unknown = 0, Local = 1, Staging = 2, Production = 3 }
