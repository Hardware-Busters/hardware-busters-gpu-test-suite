using System.Threading;
using System.Windows;
using GpuSuite.App;
using GpuSuite.App.Views;
using Xunit;

namespace GpuSuite.Tests;

public sealed class AuthorStudioViewXamlTests
{
    [Fact]
    public void AuthorStudioViewLoadsWithApplicationResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new GpuSuite.App.App();
                app.InitializeComponent();
                _ = new AuthorStudioView();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
