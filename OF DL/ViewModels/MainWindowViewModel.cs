using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Reactive.Concurrency;
using OF_DL.Entities;
using OF_DL.Exceptions;
using ReactiveUI;
using Serilog;

namespace OF_DL.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    #region Public Properties

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isLoadingAccount = true;
    [ObservableProperty] private bool _isLoadingSubscriptions = false;
    [ObservableProperty] private bool _hasSubscriptionsLoaded = false;
    [ObservableProperty] private bool _hasAuthenticationFailed = false;

    [ObservableProperty]
    private ObservableCollection<Subscription> _subscriptionsList = [];

    #endregion

    private readonly AppCommon? _appCommon;

    public MainWindowViewModel()
    {
        try
        {
            _appCommon = new AppCommon();
            RxApp.MainThreadScheduler.Schedule(LoadSubscriptions);
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
            Log.Error(ex, "Failed to initialize");
        }
    }

    private async void LoadSubscriptions()
    {
        if (_appCommon == null) return;

        try
        {
            await _appCommon.GetUser();
            await _appCommon.CreateOrUpdateUsersDatabase();
            IsLoadingAccount = false;

            IsLoadingSubscriptions = true;
            var subscriptions = await _appCommon.GetSubscriptions();

            Log.Information($"Found {subscriptions.Count} subscriptions");

            var subscriptionsList = new ObservableCollection<Subscription>();
            foreach (var (key, value) in subscriptions)
            {
                subscriptionsList.Add(new Subscription(key, value));
            }

            SubscriptionsList = subscriptionsList;
            IsLoadingSubscriptions = false;
            HasSubscriptionsLoaded = true;
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
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load subscriptions");
        }

        IsLoading = false;
    }
}
