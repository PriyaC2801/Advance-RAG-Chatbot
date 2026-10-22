namespace AdvancedRag.Core.Rag;

/// <summary>The vector database cannot be reached or rejected a command. Mapped to HTTP 503.</summary>
public sealed class VectorStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The document has not been indexed yet (or the index does not match the current settings). Mapped to HTTP 409.</summary>
public sealed class IndexNotReadyException(string message) : Exception(message);

/// <summary>The request conflicts with current state, for example an ingestion is already running. Mapped to HTTP 409.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>The caller sent something invalid (empty question, unknown mode). Mapped to HTTP 400.</summary>
public sealed class BadRequestException(string message) : Exception(message);
