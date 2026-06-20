using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class LoginWindow : Window
    {
        private bool _errorShown;

        public LoginWindow()
        {
            InitializeComponent();
        }

        public void MoveLogin(double x, double y)
        {
            Position = new Avalonia.PixelPoint((int)x, (int)y);
            Show();
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Avalonia.Visual)sender);
                if (pos.Y > 26) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailBox.Text?.Trim();
            string password = PasswordBox.Text;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ErrorText.Text = "Please enter both email and password.";
                return;
            }

            ErrorText.Text = "";
            LoginBtn.IsEnabled = false;
            LoginBtn.Content = "Logging in...";

            try
            {
                if (AppMain.dataBase != null)
                {
                    AppMain.dataBase.rememberMe = RememberCheckbox.IsChecked == true;
                    await AppMain.dataBase.GetUserLogin(email, password);
                    AppMain.AddLog("Login successful");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (App.MainWindowInstance is MainWindow mw)
                            mw.LoggedIn();
                    });
                    App.StartAfkTimer();

                    EmailBox.Text = "";
                    PasswordBox.Text = "";
                    await Dispatcher.UIThread.InvokeAsync(() => Hide());
                }
                else
                {
                    ErrorText.Text = "Database not initialized yet.";
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Login failed: " + ex.Message);

                string statusMessage;
                byte statusSeverity;
                string msg = ex.Message;

                if (msg.Contains("email"))
                {
                    if (msg.Contains("app.form.invalid"))
                    {
                        statusMessage = "Invalid email form";
                        statusSeverity = 2;
                    }
                    else
                    {
                        statusMessage = "Unknown email";
                        statusSeverity = 1;
                    }
                }
                else if (msg.Contains("password"))
                {
                    statusMessage = "Wrong password";
                    statusSeverity = 1;
                }
                else if (msg.Contains("could not understand"))
                {
                    statusMessage = "Severe issue, server did not understand request";
                    statusSeverity = 1;
                }
                else
                {
                    statusMessage = "Login failed: " + msg;
                    statusSeverity = 1;
                }

                if (AppMain.dataBase != null)
                    AppMain.dataBase.JWT = null;
                ApplicationSettings.GlobalSettings.Save();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (App.MainWindowInstance is MainWindow mw)
                        mw.SignOut();
                });

                AppMain.StatusUpdate(statusMessage, statusSeverity);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ErrorText.Foreground = statusSeverity switch
                    {
                        1 => Brushes.Red,
                        2 => Brushes.Orange,
                        _ => Brushes.Yellow,
                    };
                    ErrorText.Text = statusMessage;
                    if (!_errorShown)
                    {
                        Height += 20;
                        _errorShown = true;
                    }
                    ErrorText.IsVisible = true;
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoginBtn.IsEnabled = true;
                    LoginBtn.Content = "Login";
                });
            }
        }
    }
}
