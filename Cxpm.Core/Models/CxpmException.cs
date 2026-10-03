namespace Cxpm.Core.Models;

public sealed class CxpmException(string message)
    : Exception(message);
