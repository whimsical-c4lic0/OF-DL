using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AvaloniaProgressRing;
using Serilog;

namespace OF_DL.ViewModels;

public class MainWindowViewModel
{
    public ObservableCollection<SubscriptionModel> SubscriptionsList { get; set; } = [];

    private readonly AppCommon _appCommon;

    public MainWindowViewModel()
    {
        _appCommon = new AppCommon();
        LoadSubscriptions();
    }

    private async Task LoadSubscriptions()
    {
        await _appCommon.GetUser();
        await _appCommon.CreateOrUpdateUsersDatabase();
        var subscriptions =  await _appCommon.GetSubscriptions();
        Log.Information($"Found {subscriptions.Count} subscriptions");


        var subscriptionsList = new ObservableCollection<SubscriptionModel>();
        foreach (var (key, value) in subscriptions)
        {
            subscriptionsList.Add(new SubscriptionModel(key, value));
        }
        SubscriptionsList = subscriptionsList;
    }
}
