using Majo.Runtime.Core;

namespace Majo.Runtime.Actors;

public class ActorException : MajoRuntimeException
{
    public ActorException(string message)
        : base(message)
    {
    }

    public ActorException(string message, System.Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ActorDefinitionException : ActorException
{
    public ActorDefinitionException(string actorId, string message)
        : base($"Actor definition '{actorId}' is invalid: {message}")
    {
    }
}

public sealed class DuplicateActorIdException : ActorException
{
    public DuplicateActorIdException(string actorId)
        : base($"Actor ID '{actorId}' is already active or defined more than once.")
    {
    }
}

public sealed class ActorNotFoundException : ActorException
{
    public ActorNotFoundException(string actorId)
        : base($"Actor '{actorId}' was not found.")
    {
    }
}

public sealed class ActorFactoryNotFoundException : ActorException
{
    public ActorFactoryNotFoundException(string factoryId, string actorId)
        : base($"Actor '{actorId}' requires unregistered factory '{factoryId}'.")
    {
    }
}

public sealed class ActorFactoryConflictException : ActorException
{
    public ActorFactoryConflictException(
        string actorId,
        string existingFactoryId,
        string requestedFactoryId)
        : base(
            $"Actor '{actorId}' uses factory '{existingFactoryId}' and cannot be used " +
            $"as '{requestedFactoryId}'.")
    {
    }
}

public sealed class DuplicateActorFactoryException : ActorException
{
    public DuplicateActorFactoryException(string factoryId)
        : base($"Actor factory '{factoryId}' is already registered.")
    {
    }
}

public sealed class UnknownActorAppearanceException : ActorException
{
    public UnknownActorAppearanceException(string actorId, string appearance)
        : base($"Actor '{actorId}' does not define appearance '{appearance}'.")
    {
    }
}
