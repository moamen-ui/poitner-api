using System.Runtime.CompilerServices;

// Lets Tests call RetentionService.SweepOnceAsync (internal — see DB-08 execution doc §5) directly
// against a Sqlite-backed AppDbContext, without standing up the whole hosted service.
[assembly: InternalsVisibleTo("Pointer.Tests")]
