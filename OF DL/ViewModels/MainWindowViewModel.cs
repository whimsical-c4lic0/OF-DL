using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Reactive.Concurrency;
using OF_DL.Exceptions;
using ReactiveUI;
using Serilog;

namespace OF_DL.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    #region Public Properties

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasSubscriptionsLoaded = false;
    [ObservableProperty] private bool _hasAuthenticationFailed = false;

    [ObservableProperty]
    private ObservableCollection<SubscriptionModel> _subscriptionsList = [];

    #endregion

    private readonly AppCommon _appCommon = new AppCommon();

    public MainWindowViewModel()
    {
        RxApp.MainThreadScheduler.Schedule(LoadSubscriptions);
    }

    private async void LoadSubscriptions()
    {
        try
        {
            await _appCommon.GetUser();
            await _appCommon.CreateOrUpdateUsersDatabase();
            var subscriptions = await _appCommon.GetSubscriptions();

            Log.Information($"Found {subscriptions.Count} subscriptions");

            var subscriptionsList = new ObservableCollection<SubscriptionModel>();
            foreach (var (key, value) in subscriptions)
            {
                subscriptionsList.Add(new SubscriptionModel(key, value));
            }

            SubscriptionsList = subscriptionsList;
        }
        catch (UnsupportedOperatingSystem ex)
        {
            Log.Error(ex, ex.ToString());
            // TODO: Show error dialog (exit on confirmation)
        }
        catch (AuthenticationFailureException ex)
        {
            Log.Error(ex, ex.ToString());
            HasAuthenticationFailed = true;
        }
        catch (MissingFileException ex)
        {
            Log.Error(ex, ex.ToString());
            if (ex.Filename == "auth.json")
            {
                // Missing auth.json
                HasAuthenticationFailed = true;
            }
            else if (ex.Filename == "config.json")
            {
                // Missing config.json
                // TODO: Show a dialog to create a new config.json (OK to create new config, cancel to exit)
            }
        }
        catch (MalformedFileException ex)
        {
            Log.Error(ex, ex.ToString());
            if (ex.Filename == "auth.json")
            {
                // Malformed auth.json
                HasAuthenticationFailed = true;
            }
            else if (ex.Filename == "config.json")
            {
                // Malformed config.json
                // TODO: Show a dialog to create a new config.json (OK to create new config, cancel to exit)
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load subscriptions");
        }

        IsLoading = false;
        HasSubscriptionsLoaded = true;
    }
}
