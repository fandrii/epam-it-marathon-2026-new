namespace APIHandler.Exceptions;

public class GuestSessionCreationConflictException(string message) : ValidationException(message);
