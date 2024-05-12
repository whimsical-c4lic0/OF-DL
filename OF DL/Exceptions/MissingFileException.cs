namespace OF_DL.Exceptions;

public class MissingFileException(string filename) : Exception("File missing: " + filename)
{
    public string Filename { get; } = filename;
}
