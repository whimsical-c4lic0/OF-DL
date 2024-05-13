namespace OF_DL.Entities;

public class Subscription(string username, int id)
{
    public string Username { get; set; } = username;
    public int Id { get; set; } = id;
}
