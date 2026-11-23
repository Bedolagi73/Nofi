using System.IO;
using System.Windows;
using Nofi.Core;
using Nofi.Core.Storage;
using Nofi.UI;

namespace Nofi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Environment.GetEnvironmentVariable("NOFI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nofi");

        var clock = new SystemDateTimeProvider();
        var dispatcher = new NotificationDispatcher(clock, new EmailSenderMock(), new SmsSenderMock());
        var scheduler = new SchedulerService(new JsonTaskRepository(directory), clock, dispatcher);
        MainWindow = new MainWindow(scheduler);
        MainWindow.Show();
    }
}
