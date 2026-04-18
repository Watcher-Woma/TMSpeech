using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using TMSpeech.Core;
using TMSpeech.GUI.Views;

namespace TMSpeech.GUI.Controls;

public class TrayMenu : NativeMenu
{
    private MainWindow _mainWindow;


    public void UpdateItems()
    {
        _mainWindow = (App.Current as App).MainWindow;
        this.Items.Clear();
        if (_mainWindow.ViewModel.IsLocked)
        {
            this.Items.Add(new NativeMenuItem
                { Header = "解锁字幕", Command = ReactiveCommand.Create(UnlockCaption) });
        }

        // Translation toggle
        var enableTranslation = ConfigManagerFactory.Instance.Get<bool>(TranslatorConfigTypes.EnableTranslation);
        var translationToggle = new NativeMenuItem
        {
            Header = enableTranslation ? "关闭翻译" : "开启翻译",
            Command = ReactiveCommand.Create(ToggleTranslation)
        };
        this.Items.Add(translationToggle);

        this.Items.Add(new NativeMenuItem { Header = "重置窗口位置", Command = ReactiveCommand.Create(ResetWindowLocation) });
        this.Items.Add(new NativeMenuItem { Header = "退出", Command = ReactiveCommand.Create(Exit) });
    }

    private void ToggleTranslation()
    {
        var current = ConfigManagerFactory.Instance.Get<bool>(TranslatorConfigTypes.EnableTranslation);
        ConfigManagerFactory.Instance.Apply(TranslatorConfigTypes.EnableTranslation, !current);
    }

    private void ResetWindowLocation()
    {
        ConfigManagerFactory.Instance.DeleteAndApply<List<int>>(GeneralConfigTypes.MainWindowLocation);
        _mainWindow = (App.Current as App).MainWindow;
        _mainWindow.Position = new(100, 100);
    }

    private void Exit()
    {
        // Save window location and size.
        var left = _mainWindow.Position.X;
        var top = _mainWindow.Position.Y;
        var width = (int)_mainWindow.Width;
        var height = (int)_mainWindow.Height;
        ConfigManagerFactory.Instance.Apply<List<int>>(GeneralConfigTypes.MainWindowLocation, [left, top, width, height]);
        Environment.Exit(0);
    }

    private void UnlockCaption()
    {
        _mainWindow.ViewModel.IsLocked = false;
    }
}