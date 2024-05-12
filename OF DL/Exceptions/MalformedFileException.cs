namespace OF_DL.Exceptions;

public class MalformedFileException(string filename) : Exception("File malformed: " + filename)
{
    public string Filename { get; } = filename;
}
