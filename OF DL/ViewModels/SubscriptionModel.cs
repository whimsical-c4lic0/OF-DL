namespace OF_DL.ViewModels;

public class SubscriptionModel(string username, int id)
{
    public string Username { get; set; } = username;
    public int Id { get; set; } = id;
}
