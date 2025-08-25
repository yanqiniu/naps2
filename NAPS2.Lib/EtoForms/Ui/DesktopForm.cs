using System.Collections.Immutable;
using System.ComponentModel;
using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using NAPS2.EtoForms.Desktop;
using NAPS2.EtoForms.Layout;
using NAPS2.EtoForms.Notifications;
using NAPS2.EtoForms.Widgets;
using NAPS2.ImportExport.Images;
using NAPS2.Scan;

namespace NAPS2.EtoForms.Ui;

public abstract class DesktopForm : EtoFormBase
{
    private readonly DesktopKeyboardShortcuts _keyboardShortcuts;
    private readonly NotificationManager _notificationManager;
    private readonly CultureHelper _cultureHelper;
    protected readonly ColorScheme _colorScheme;
    protected readonly IProfileManager _profileManager;
    protected readonly ThumbnailController _thumbnailController;
    private readonly UiThumbnailProvider _thumbnailProvider;
    protected readonly DesktopController _desktopController;
    protected readonly IDesktopScanController _desktopScanController;
    private readonly ImageListActions _imageListActions;
    private readonly DesktopFormProvider _desktopFormProvider;
    private readonly IDesktopSubFormController _desktopSubFormController;
    private readonly ImageTransfer _imageTransfer = new();
    private readonly Lazy<DesktopCommands> _commands;
    private readonly Sidebar _sidebar;
    protected readonly IIconProvider _iconProvider;

    protected readonly ListProvider<Command> _scanMenuCommands = new();
    private readonly ListProvider<Command> _languageMenuCommands = new();
    protected readonly ListProvider<Command> _editWithCommands = new();
    private readonly ContextMenu _contextMenu = new();

    private readonly NotificationArea _notificationArea;
    protected IListView<UiImage> _listView;
    private ImageListSyncer? _imageListSyncer;

    public DesktopForm(
        Naps2Config config,
        DesktopKeyboardShortcuts keyboardShortcuts,
        NotificationManager notificationManager,
        CultureHelper cultureHelper,
        ColorScheme colorScheme,
        IProfileManager profileManager,
        UiImageList imageList,
        ThumbnailController thumbnailController,
        UiThumbnailProvider thumbnailProvider,
        DesktopController desktopController,
        IDesktopScanController desktopScanController,
        ImageListActions imageListActions,
        ImageListViewBehavior imageListViewBehavior,
        DesktopFormProvider desktopFormProvider,
        IDesktopSubFormController desktopSubFormController,
        Lazy<DesktopCommands> commands,
        Sidebar sidebar,
        IIconProvider iconProvider) : base(config)
    {
        Icon = EtoPlatform.Current.IsGtk ? new Icon(1f, Icons.scanner_128.ToEtoImage()) : Icons.favicon.ToEtoIcon();

        _keyboardShortcuts = keyboardShortcuts;
        _notificationManager = notificationManager;
        _cultureHelper = cultureHelper;
        _colorScheme = colorScheme;
        _profileManager = profileManager;
        ImageList = imageList;
        _thumbnailController = thumbnailController;
        _thumbnailProvider = thumbnailProvider;
        _desktopController = desktopController;
        _desktopScanController = desktopScanController;
        _imageListActions = imageListActions;
        _desktopFormProvider = desktopFormProvider;
        _desktopSubFormController = desktopSubFormController;
        _sidebar = sidebar;
        _iconProvider = iconProvider;
        _commands = commands;

        _desktopFormProvider.DesktopForm = this;
        _keyboardShortcuts.Assign(Commands);
        CreateToolbarsAndMenus();
        UpdateScanButton();
        EditWithAppChanged();
        UpdateProfilesToolbar();
        InitLanguageDropdown();

        _listView = EtoPlatform.Current.CreateListView(imageListViewBehavior);
        _listView.Selection = ImageList.Selection;
        _listView.ItemClicked += ListViewItemClicked;
        _listView.Drop += ListViewDrop;
        _listView.SelectionChanged += ListViewSelectionChanged;
        _listView.ImageSize = new Size(_thumbnailController.VisibleSize, _thumbnailController.VisibleSize);
        _listView.ContextMenu = _contextMenu;

        // TODO: Fix Eto so that we don't need to set an item here (otherwise the first time we right click nothing happens)
        _contextMenu.Items.Add(Commands.SelectAll);
        _contextMenu.Opening += OpeningContextMenu;
        EtoPlatform.Current.AttachMouseWheelEvent(_listView.Control, ListViewMouseWheel);
        EtoPlatform.Current.AttachMouseMoveEvent(_listView.Control, ListViewMouseMove);
        EtoPlatform.Current.HandleKeyDown(this, _keyboardShortcuts.Perform);
        EtoPlatform.Current.HandleKeyDown(_listView.Control, _keyboardShortcuts.Perform);

        //
        // Shown += FDesktop_Shown;
        // Closing += FDesktop_Closing;
        // Closed += FDesktop_Closed;
        _thumbnailController.ListView = _listView;
        _thumbnailController.ThumbnailSizeChanged += ThumbnailController_ThumbnailSizeChanged;
        EtoPlatform.Current.AttachDpiDependency(this, scale => _thumbnailController.Oversample = scale);
        ImageList.SelectionChanged += ImageList_SelectionChanged;
        ImageList.ImagesUpdated += ImageList_ImagesUpdated;
        ImageList.ImagesThumbnailInvalidated += ImageList_ImagesThumbnailInvalidated;
        _profileManager.ProfilesUpdated += ProfileManager_ProfilesUpdated;
        _notificationArea = new NotificationArea(_notificationManager, LayoutController);
    }

    protected override void BuildLayout()
    {
        FormStateController.AutoLayoutSize = false;
        FormStateController.DefaultClientSize = new Size(1210, 600);
        EtoPlatform.Current.AttachDpiDependency(this, _ =>
            MinimumSize = Size.Round(new SizeF(600, 300) * EtoPlatform.Current.GetLayoutScaleFactor(this)));

        LayoutController.RootPadding = 0;
        LayoutController.Content = L.LeftPanel(
            Config.Get(c => c.HiddenButtons).HasFlag(ToolbarButtons.Sidebar)
                ? C.None()
                : _sidebar.CreateView(this),
            L.Overlay(
                // For WinForms, we add 1px of top padding to give us room to draw a border above the listview
                _listView.Control.Padding(top: EtoPlatform.Current.IsWinForms ? 1 : 0),
                L.Column(
                    C.Filler(),
                    L.Row(
                        GetControlButtons(),
                        C.Filler(),
                        _notificationArea.Content)
                ).Padding(8)
            ).Scale()
        ).SizeConfig(
            () => Config.Get(c => c.SidebarWidth),
            width => Config.User.Set(c => c.SidebarWidth, width),
            200);
    }

    private void OpeningContextMenu(object? sender, EventArgs e)
    {
        _contextMenu.Items.Clear();
        if (!EtoPlatform.Current.IsMac)
        {
            // TODO: Can't do this on Mac yet as it disables the menu item indefinitely
            Commands.Paste.Enabled = _imageTransfer.IsInClipboard() || Clipboard.Instance.ContainsImage;
        }
        if (ImageList.Selection.Any())
        {
            // TODO: Is this memory leaking (because of event handlers) when commands are converted to menuitems?
            _contextMenu.Items.AddRange(
            [
                C.ButtonMenuItem(this, Commands.ViewImage),
                new SeparatorMenuItem(),
                C.ButtonMenuItem(this, Commands.SelectAll),
                C.ButtonMenuItem(this, Commands.Copy),
                C.ButtonMenuItem(this, Commands.Paste),
                new SeparatorMenuItem(),
                C.ButtonMenuItem(this, Commands.Undo),
                C.ButtonMenuItem(this, Commands.Redo),
                new SeparatorMenuItem(),
                C.ButtonMenuItem(this, Commands.Delete)
            ]);
        }
        else
        {
            _contextMenu.Items.AddRange(
            [
                C.ButtonMenuItem(this, Commands.SelectAll),
                C.ButtonMenuItem(this, Commands.Paste),
                new SeparatorMenuItem(),
                C.ButtonMenuItem(this, Commands.Undo),
                C.ButtonMenuItem(this, Commands.Redo)
            ]);
        }
    }

    private void ImageList_SelectionChanged(object? sender, EventArgs e)
    {
        Invoker.Current.InvokeDispatch(() =>
        {
            UpdateToolbar();
            _listView.Selection = ImageList.Selection;
        });
    }

    private void ImageList_ImagesUpdated(object? sender, ImageListEventArgs e)
    {
        Invoker.Current.InvokeDispatch(UpdateToolbar);
    }

    private void ImageList_ImagesThumbnailInvalidated(object? sender, ImageListEventArgs e)
    {
        Invoker.Current.InvokeDispatch(UpdateToolbar);
    }

    private void ProfileManager_ProfilesUpdated(object? sender, EventArgs e)
    {
        UpdateScanButton();
        UpdateProfilesToolbar();
    }

    private void ThumbnailController_ThumbnailSizeChanged(object? sender, EventArgs e)
    {
        SetThumbnailSpacing(_thumbnailController.VisibleSize, EtoPlatform.Current.GetScaleFactor(this));
        UpdateToolbar();
    }

    protected UiImageList ImageList { get; }
    protected DesktopCommands Commands => _commands.Value;

    public void ReassignKeyboardShortcuts()
    {
        _keyboardShortcuts.Assign(Commands);
        UpdateScanButton();
        RecreateToolbarsAndMenus();
    }

    protected virtual void RecreateToolbarsAndMenus() => CreateToolbarsAndMenus();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _imageListSyncer = new ImageListSyncer(ImageList, _listView.ApplyDiffs, SynchronizationContext.Current!);
        EtoPlatform.Current.AttachDpiDependency(_listView.Control,
            scale => SetThumbnailSpacing(_thumbnailController.VisibleSize, scale));
        _desktopController.PreInitialize();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateToolbar();
        await _desktopController.Initialize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_desktopController.PrepareForClosing(true))
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _desktopController.Cleanup();

        // TODO: Make sure we don't have any remaining memory leaks (toolbars? commands?)
        _thumbnailController.ThumbnailSizeChanged -= ThumbnailController_ThumbnailSizeChanged;
        ImageList.SelectionChanged -= ImageList_SelectionChanged;
        ImageList.ImagesUpdated -= ImageList_ImagesUpdated;
        ImageList.ImagesThumbnailInvalidated -= ImageList_ImagesThumbnailInvalidated;
        _profileManager.ProfilesUpdated -= ProfileManager_ProfilesUpdated;
        _notificationArea.Dispose();
        _imageListSyncer?.Dispose();
    }

    protected virtual void CreateToolbarsAndMenus()
    {
        if (ToolBar == null)
        {
            ToolBar = new ToolBar();
            ConfigureToolbars();
        }
        else
        {
            ToolBar.Items.Clear();
        }

        var hiddenButtons = Config.Get(c => c.HiddenButtons);

        if (!hiddenButtons.HasFlag(ToolbarButtons.Scan))
            CreateToolbarButtonWithMenu(Commands.Scan, DesktopToolbarMenuType.Scan, new MenuProvider()
                .Dynamic(_scanMenuCommands)
                .Separator()
                .Append(Commands.NewProfile)
                .Append(Commands.BatchScan)
                .Append(Config.Get(c => c.DisableScannerSharing) ? null : Commands.ScannerSharing));
        if (!hiddenButtons.HasFlag(ToolbarButtons.Profiles))
            CreateToolbarButton(Commands.Profiles);
        if (!hiddenButtons.HasFlag(ToolbarButtons.Ocr))
            CreateToolbarButton(Commands.Ocr);
        if (!hiddenButtons.HasFlag(ToolbarButtons.Import))
            CreateToolbarButton(Commands.Import);
        CreateToolbarSeparator();
        if (!hiddenButtons.HasFlag(ToolbarButtons.SavePdf))
            CreateToolbarButtonWithMenu(Commands.SavePdf, DesktopToolbarMenuType.SavePdf, new MenuProvider()
                .Append(Commands.SaveAllPdf)
                .Append(Commands.SaveSelectedPdf)
                .Separator()
                .Append(Commands.PdfSettings));
        if (!hiddenButtons.HasFlag(ToolbarButtons.SaveImages))
            CreateToolbarButtonWithMenu(Commands.SaveImages, DesktopToolbarMenuType.SaveImages, new MenuProvider()
                .Append(Commands.SaveAllImages)
                .Append(Commands.SaveSelectedImages)
                .Separator()
                .Append(Commands.ImageSettings));
        if (!hiddenButtons.HasFlag(ToolbarButtons.EmailPdf) && PlatformCompat.System.CanEmail)
            CreateToolbarButtonWithMenu(Commands.EmailPdf, DesktopToolbarMenuType.EmailPdf, new MenuProvider()
                .Append(Commands.EmailAll)
                .Append(Commands.EmailSelected)
                .Separator()
                .Append(Commands.EmailSettings)
                .Append(Commands.PdfSettings));
        if (!hiddenButtons.HasFlag(ToolbarButtons.Print) && PlatformCompat.System.CanPrint)
            CreateToolbarButton(Commands.Print);
        CreateToolbarSeparator();
        if (!hiddenButtons.HasFlag(ToolbarButtons.Image))
            CreateToolbarMenu(Commands.ImageMenu, new MenuProvider()
                .Append(Commands.ViewImage)
                .Separator()
                .Append(Commands.Crop)
                .Append(Commands.BrightCont)
                .Append(Commands.HueSat)
                .Append(Commands.BlackWhite)
                .Append(Commands.Sharpen)
                .Append(Commands.DocumentCorrection)
                .Separator()
                .Append(Commands.Split)
                .Append(Commands.Combine)
                .Separator()
                .Dynamic(_editWithCommands)
                .Append(Commands.EditWithPick)
                .Separator()
                .Append(Commands.ResetImage));
        if (!hiddenButtons.HasFlag(ToolbarButtons.Rotate))
            CreateToolbarMenu(Commands.RotateMenu, GetRotateMenuProvider());
        if (!hiddenButtons.HasFlag(ToolbarButtons.Move))
            CreateToolbarStackedButtons(Commands.MoveUp, Commands.MoveDown);
        if (!hiddenButtons.HasFlag(ToolbarButtons.Reorder))
            CreateToolbarMenu(Commands.ReorderMenu, new MenuProvider()
                .Append(Commands.Duplex)
                .Separator()
                .Append(Commands.Interleave)
                .Append(Commands.Deinterleave)
                .Separator()
                .Append(Commands.AltInterleave)
                .Append(Commands.AltDeinterleave)
                .Separator()
                .SubMenu(Commands.ReverseMenu, new MenuProvider()
                    .Append(Commands.ReverseAll)
                    .Append(Commands.ReverseSelected)));
        CreateToolbarSeparator();
        if (!hiddenButtons.HasFlag(ToolbarButtons.Delete))
            CreateToolbarButton(Commands.Delete);
        if (!hiddenButtons.HasFlag(ToolbarButtons.Clear))
            CreateToolbarButton(Commands.ClearAll);
        CreateToolbarSeparator();
        if (!hiddenButtons.HasFlag(ToolbarButtons.Language))
            CreateToolbarMenu(Commands.LanguageMenu, GetLanguageMenuProvider());
        MaybeCreateToolbarStackedButtons(
            Commands.Settings, !hiddenButtons.HasFlag(ToolbarButtons.Settings),
            Commands.About, !hiddenButtons.HasFlag(ToolbarButtons.About));
    }

    private void MaybeCreateToolbarStackedButtons(Command command1, bool show1, Command command2, bool show2)
    {
        if (show1 && show2)
        {
            CreateToolbarStackedButtons(command1, command2);
        }
        else if (show1)
        {
            CreateToolbarButton(command1);
        }
        else if (show2)
        {
            CreateToolbarButton(command2);
        }
    }

    public virtual void ShowToolbarMenu(DesktopToolbarMenuType menuType)
    {
    }

    protected MenuProvider GetRotateMenuProvider() =>
        new MenuProvider()
            .Append(Commands.RotateLeft)
            .Append(Commands.RotateRight)
            .Append(Commands.Flip)
            .Append(Commands.Deskew)
            .Append(Commands.CustomRotate);

    protected MenuProvider GetLanguageMenuProvider()
    {
        return new MenuProvider().Dynamic(_languageMenuCommands);
    }

    protected virtual void ConfigureToolbars()
    {
    }

    protected virtual void UpdateProfilesToolbar()
    {
    }

    public virtual void PlaceProfilesToolbar()
    {
    }

    protected virtual void CreateToolbarButton(Command command) => throw new InvalidOperationException();

    protected virtual void CreateToolbarButtonWithMenu(Command command, DesktopToolbarMenuType menuType,
        MenuProvider menu) =>
        throw new InvalidOperationException();

    protected virtual void CreateToolbarMenu(Command command, MenuProvider menu) =>
        throw new InvalidOperationException();

    protected virtual void CreateToolbarStackedButtons(Command command1, Command command2) =>
        throw new InvalidOperationException();

    protected virtual void CreateToolbarSeparator() => throw new InvalidOperationException();

    // TODO: Can we generalize this kind of logic?
    protected SubMenuItem CreateSubMenu(Command menuCommand, MenuProvider menuProvider)
    {
        var menuItem = new SubMenuItem
        {
            Text = menuCommand.MenuText
        };
        EtoPlatform.Current.AttachDpiDependency(this,
            scale => menuItem.Image = ((ActionCommand) menuCommand).GetIconImage(scale));
        menuProvider.Handle(subItems =>
        {
            menuItem.Items.Clear();
            foreach (var subItem in subItems)
            {
                switch (subItem)
                {
                    case MenuProvider.CommandItem { Command: var command }:
                        menuItem.Items.Add(C.ButtonMenuItem(this, (ActionCommand) command));
                        break;
                    case MenuProvider.SeparatorItem:
                        menuItem.Items.Add(new SeparatorMenuItem());
                        break;
                    case MenuProvider.SubMenuItem { Command: var command, MenuProvider: var subMenuProvider }:
                        menuItem.Items.Add(CreateSubMenu(command, subMenuProvider));
                        break;
                }
            }
        });
        return menuItem;
    }

    protected virtual LayoutElement GetControlButtons()
    {
        return L.Row(GetSidebarButton(), GetZoomButtons());
    }

    protected LayoutElement GetSidebarButton()
    {
        if (Config.Get(c => c.HiddenButtons).HasFlag(ToolbarButtons.Sidebar))
        {
            return C.None();
        }
        var toggleSidebar = C.ImageButton(Commands.ToggleSidebar);
        EtoPlatform.Current.ConfigureZoomButton(toggleSidebar, "application_side_list_small");
        return toggleSidebar.AlignTrailing();
    }

    protected LayoutElement GetZoomButtons()
    {
        var zoomIn = C.ImageButton(Commands.ZoomIn);
        EtoPlatform.Current.ConfigureZoomButton(zoomIn, "zoom_in_small");
        var zoomOut = C.ImageButton(Commands.ZoomOut);
        EtoPlatform.Current.ConfigureZoomButton(zoomOut, "zoom_out_small");
        return L.Row(zoomOut.AlignTrailing(), zoomIn.AlignTrailing()).Spacing(-1);
    }

    private void InitLanguageDropdown()
    {
        _languageMenuCommands.Value = _cultureHelper.GetAvailableCultures().Select(x =>
            new ActionCommand(() => SetCulture(x.langCode))
            {
                MenuText = x.langName
            }).ToImmutableList<Command>();
    }

    protected virtual void SetCulture(string cultureId)
    {
        _desktopController.Suspend();
        try
        {
            Config.User.Set(c => c.Culture, cultureId);
            _cultureHelper.SetCulturesFromConfig();
            FormStateController.DoSaveFormState();
            var newDesktop = FormFactory.Create<DesktopForm>();
            newDesktop.Show();
            Application.Instance.MainForm = newDesktop;
            Close();
        }
        finally
        {
            _desktopController.Resume();
        }
        // TODO: If we make any other forms non-modal, we will need to refresh them too
    }

    protected virtual void UpdateToolbar()
    {
        // Top-level toolbar items
        Commands.ImageMenu.Enabled =
            Commands.RotateMenu.Enabled = Commands.MoveUp.Enabled = Commands.MoveDown.Enabled =
                Commands.Delete.Enabled = ImageList.Selection.Any();
        Commands.SavePdf.Enabled = Commands.SaveImages.Enabled = Commands.ClearAll.Enabled =
            Commands.ReorderMenu.Enabled =
                Commands.EmailPdf.Enabled = Commands.Print.Enabled = ImageList.Images.Any();

        // "All" dropdown items
        Commands.SaveAllPdf.Text = Commands.SaveAllImages.Text = Commands.EmailAll.Text =
            Commands.ReverseAll.Text = string.Format(MiscResources.AllCount, ImageList.Images.Count);
        Commands.SaveAllPdf.Enabled = Commands.SaveAllImages.Enabled = Commands.SaveAll.Enabled =
            Commands.EmailAll.Enabled = Commands.ReverseAll.Enabled = ImageList.Images.Any();

        // "Selected" dropdown items
        Commands.SaveSelectedPdf.Text = Commands.SaveSelectedImages.Text = Commands.EmailSelected.Text =
            Commands.ReverseSelected.Text = string.Format(MiscResources.SelectedCount, ImageList.Selection.Count);
        Commands.SaveSelectedPdf.Enabled = Commands.SaveSelectedImages.Enabled = Commands.SaveSelected.Enabled =
            Commands.EmailSelected.Enabled = Commands.ReverseSelected.Enabled = ImageList.Selection.Any();

        // Other
        Commands.SelectAll.Enabled = ImageList.Images.Any();
        Commands.Undo.Enabled = ImageList.CanUndo;
        Commands.Redo.Enabled = ImageList.CanRedo;
        // TODO: Set undo/redo text here (e.g. "Undo Brightness/Contrast" instead of just "Undo")
        Commands.ZoomIn.Enabled = ImageList.Images.Any() && _thumbnailController.VisibleSize < ThumbnailSizes.MAX_SIZE;
        Commands.ZoomOut.Enabled = ImageList.Images.Any() && _thumbnailController.VisibleSize > ThumbnailSizes.MIN_SIZE;
        Commands.NewProfile.Enabled =
            !(Config.Get(c => c.NoUserProfiles) && _profileManager.Profiles.Any(x => x.IsLocked));
        Commands.Combine.Enabled = ImageList.Images.Count > 1;
    }

    private void UpdateScanButton()
    {
        var defaultProfile = _profileManager.DefaultProfile;
        UpdateTitle(defaultProfile);
        var commandList = _profileManager.Profiles.Select(profile =>
                new ActionCommand(() => _desktopScanController.ScanWithProfile(profile))
                {
                    MenuText = profile.DisplayName.Replace("&", "&&"),
                    IconName = profile == defaultProfile ? "accept_small" : null
                })
            .ToImmutableList<Command>();
        for (int i = 0; i < commandList.Count; i++)
        {
            _keyboardShortcuts.AssignProfileShortcut(i + 1, commandList[i]);
        }
        _scanMenuCommands.Value = commandList;
    }

    public void EditWithAppChanged()
    {
        var appName = Config.Get(c => c.EditWithAppName);
        if (!string.IsNullOrEmpty(appName))
        {
            Commands.EditWithApp.Text = string.Format(UiStrings.EditWithAppName, appName);
            _editWithCommands.Value = ImmutableList.Create((Command) Commands.EditWithApp);
        }
        else
        {
            _editWithCommands.Value = ImmutableList<Command>.Empty;
        }
    }

    protected virtual void UpdateTitle(ScanProfile? defaultProfile)
    {
        Title = string.Format(UiStrings.Naps2TitleFormat, defaultProfile?.DisplayName ?? UiStrings.Naps2FullName);
    }

    private void ListViewMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Modifiers.HasFlag(Keys.Control))
        {
            _thumbnailController.StepSize(e.Delta.Height);
            e.Handled = true;
        }
    }

    private void ListViewMouseMove(object? sender, MouseEventArgs e)
    {
        _notificationManager.StartTimers();
    }

    protected virtual void SetThumbnailSpacing(int thumbnailSize, float scale)
    {
    }

    private void ListViewItemClicked(object? sender, EventArgs e) => _desktopSubFormController.ShowViewerForm();

    private void ListViewSelectionChanged(object? sender, EventArgs e)
    {
        ImageList.UpdateSelection(_listView.Selection);
        UpdateToolbar();
    }

    private void ListViewDrop(object? sender, DropEventArgs args)
    {
        if (args.CustomData != null)
        {
            var data = _imageTransfer.FromBinaryData(args.CustomData);
            if (data.ProcessId == Process.GetCurrentProcess().Id)
            {
                DragMoveImages(args.Position);
            }
            else
            {
                _desktopController.ImportDirect(data, false);
            }
        }
        else if (args.FilePaths != null)
        {
            _desktopController.ImportFiles(args.FilePaths);
        }
    }

    private void DragMoveImages(int position)
    {
        if (!ImageList.Selection.Any())
        {
            return;
        }
        if (position != -1)
        {
            _imageListActions.MoveTo(position);
        }
    }

    public void ToggleSidebar()
    {
        _sidebar.ToggleVisibility();
    }
}