using Serilog;
using Spectre.Console;

namespace OF_DL;

public static class ConsoleApp
{
    public static async Task Run()
    {
        try
        {
            var common = new AppCommon();
            await common.GetUser();
            await common.CreateOrUpdateUsersDatabase();
        }
        catch (Exception ex)
        {
            Log.Error("Exception caught: {0}\n\nStackTrace: {1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null)
            {
                Log.Error("Inner Exception: {0}\n\nStackTrace: {1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }

    }
}
