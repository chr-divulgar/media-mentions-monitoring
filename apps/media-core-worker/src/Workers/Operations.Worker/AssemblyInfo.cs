using System.Runtime.CompilerServices;

// Lets MediaOpsCore.UnitTests exercise internal-only members (e.g. ChunkTranscriptionPipeline's
// StripTextPrefixOverlap) without widening their accessibility to public.
[assembly: InternalsVisibleTo("MediaOpsCore.UnitTests")]
