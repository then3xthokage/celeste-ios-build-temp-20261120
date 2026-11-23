using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using UIKit;

namespace celestemeow
{
    internal sealed class ModManagerViewController : UIViewController
    {
        private readonly Action playGame;
        private UIStackView page;
        private UIStackView rows;
        private UIScrollView scroll;
        private UITextField searchField;
        private UILabel statusLabel;
        private UIView searchContainer;
        private UIButton playButton;
        private UIButton shareLogsButton;
        private string currentPage = "installed";
        private bool startingGame;
        private bool modActionInProgress;

        public ModManagerViewController(Action playGame)
        {
            this.playGame = playGame;
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View.BackgroundColor = new UIColor(0.055f, 0.065f, 0.085f, 1f);

            page = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 12,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            View.AddSubview(page);
            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                page.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 12),
                page.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -12),
                page.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor, 18),
                page.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor, -18)
            });

            UILabel title = new UILabel
            {
                Text = "CELESTE  ·  MODS",
                TextColor = UIColor.White,
                Font = UIFont.BoldSystemFontOfSize(28),
                Lines = 1
            };
            page.AddArrangedSubview(title);

            UILabel subtitle = new UILabel
            {
                Text = "Install and manage Everest mods before you play.",
                TextColor = new UIColor(0.72f, 0.75f, 0.82f, 1f),
                Font = UIFont.SystemFontOfSize(15),
                Lines = 1
            };
            page.AddArrangedSubview(subtitle);

            UIStackView navigation = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.FillEqually,
                Spacing = 8
            };
            navigation.AddArrangedSubview(CreateButton("Installed", delegate { ShowInstalled(); }));
            navigation.AddArrangedSubview(CreateButton("Discover mods", delegate { BrowseCatalog(""); }));
            navigation.AddArrangedSubview(CreateButton("Update mods", delegate { UpdateMods(); }));
            playButton = CreateButton("Play", delegate { StartGame(); });
            playButton.BackgroundColor = new UIColor(0.78f, 0.24f, 0.30f, 1f);
            navigation.AddArrangedSubview(playButton);
            page.AddArrangedSubview(navigation);
            navigation.HeightAnchor.ConstraintEqualTo(46).Active = true;

            shareLogsButton = CreateButton("Share logs", ShareDiagnostics);
            page.AddArrangedSubview(shareLogsButton);
            shareLogsButton.HeightAnchor.ConstraintEqualTo(40).Active = true;

            UIStackView updatePreference = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Center,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 10
            };
            UILabel updatePreferenceLabel = CreateLabel("Automatically update mods before Play", 14, false, new UIColor(0.82f, 0.84f, 0.89f, 1f));
            updatePreference.AddArrangedSubview(updatePreferenceLabel);
            UISwitch autoUpdate = new UISwitch { On = IOSModLibrary.AutoUpdateOnLaunch };
            autoUpdate.ValueChanged += delegate
            {
                IOSModLibrary.SetAutoUpdateOnLaunch(autoUpdate.On);
                SetStatus(autoUpdate.On ? "Mods will be checked for updates before Play." : "Automatic mod updates are off.");
            };
            updatePreference.AddArrangedSubview(autoUpdate);
            page.AddArrangedSubview(updatePreference);

            searchContainer = CreateSearch();
            page.AddArrangedSubview(searchContainer);

            statusLabel = new UILabel
            {
                Text = "Mods are stored privately on this device.",
                TextColor = new UIColor(0.72f, 0.75f, 0.82f, 1f),
                Font = UIFont.SystemFontOfSize(13),
                Lines = 0
            };
            page.AddArrangedSubview(statusLabel);

            scroll = new UIScrollView
            {
                AlwaysBounceVertical = true,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            page.AddArrangedSubview(scroll);
            scroll.SetContentHuggingPriority(249, UILayoutConstraintAxis.Vertical);
            scroll.SetContentCompressionResistancePriority(749, UILayoutConstraintAxis.Vertical);

            rows = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 10,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            scroll.AddSubview(rows);
            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                rows.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 4),
                rows.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -8),
                rows.LeadingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.LeadingAnchor),
                rows.TrailingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.TrailingAnchor)
            });

            ShowInstalled();
        }

        private UIView CreateSearch()
        {
            UIStackView container = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 8
            };
            searchField = new UITextField
            {
                Placeholder = "Search Everest mods",
                BorderStyle = UITextBorderStyle.RoundedRect,
                ReturnKeyType = UIReturnKeyType.Search,
                AutocorrectionType = UITextAutocorrectionType.No,
                AutocapitalizationType = UITextAutocapitalizationType.None,
                ClearButtonMode = UITextFieldViewMode.WhileEditing
            };
            searchField.EditingDidEndOnExit += delegate { BrowseCatalog(searchField.Text); };
            container.AddArrangedSubview(searchField);
            UIButton search = CreateButton("Search", delegate { BrowseCatalog(searchField.Text); });
            container.AddArrangedSubview(search);
            search.WidthAnchor.ConstraintEqualTo(110).Active = true;
            container.HeightAnchor.ConstraintEqualTo(42).Active = true;
            return container;
        }

        private UIButton CreateButton(string title, Action action)
        {
            UIButton button = new UIButton(UIButtonType.System);
            button.SetTitle(title, UIControlState.Normal);
            button.SetTitleColor(UIColor.White, UIControlState.Normal);
            button.BackgroundColor = new UIColor(0.16f, 0.18f, 0.23f, 1f);
            button.TitleLabel.Font = UIFont.BoldSystemFontOfSize(14);
            button.Layer.CornerRadius = 9;
            button.ClipsToBounds = true;
            button.TouchUpInside += delegate { action(); };
            return button;
        }

        private void ClearRows()
        {
            foreach (UIView view in rows.ArrangedSubviews.ToArray())
            {
                rows.RemoveArrangedSubview(view);
                view.RemoveFromSuperview();
            }
        }

        private void ShowInstalled()
        {
            currentPage = "installed";
            searchContainer.Hidden = true;
            ClearRows();
            List<InstalledMod> mods;
            try
            {
                mods = IOSModLibrary.GetInstalledMods();
            }
            catch (Exception exception)
            {
                SetStatus("Could not read installed mods: " + exception.Message, true);
                return;
            }
            if (mods.Count == 0)
            {
                AddMessage("No mods yet. Open Discover mods to browse Everest packages.");
                return;
            }
            foreach (InstalledMod mod in mods) rows.AddArrangedSubview(CreateInstalledRow(mod));
            SetStatus("Required dependencies and load order are checked before launch. Automatic updates can be changed above.");
        }

        private UIView CreateInstalledRow(InstalledMod mod)
        {
            UIStackView row = CreateRow();
            UIStackView details = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 4
            };
            details.AddArrangedSubview(CreateLabel(mod.Name, 17, true, UIColor.White));
            UIColor statusColor = !mod.Supported || mod.DependenciesMissing
                ? new UIColor(0.92f, 0.66f, 0.48f, 1f)
                : new UIColor(0.64f, 0.78f, 0.68f, 1f);
            details.AddArrangedSubview(CreateLabel(mod.Status, 13, false, statusColor));
            row.AddArrangedSubview(details);

            UISwitch enabled = new UISwitch { On = mod.Enabled, Enabled = mod.Supported };
            enabled.ValueChanged += async delegate
            {
                if (!BeginModAction())
                {
                    enabled.SetState(mod.Enabled, true);
                    return;
                }
                bool requestedState = enabled.On;
                enabled.Enabled = false;
                try
                {
                    IProgress<string> progress = new Progress<string>(message => SetStatus(message));
                    List<string> alsoDisabled = await IOSModLibrary.SetEnabledAsync(mod.Name, requestedState, progress);
                    string message = requestedState
                        ? mod.Name + " will be prepared when you play."
                        : mod.Name + " is disabled.";
                    if (!requestedState && alsoDisabled.Count > 0)
                        message += " Also disabled mods that require it: " + string.Join(", ", alsoDisabled) + ".";
                    ShowInstalled();
                    SetStatus(message);
                }
                catch (Exception exception)
                {
                    enabled.SetState(mod.Enabled, true);
                    enabled.Enabled = true;
                    SetStatus(exception.Message, true);
                }
                finally
                {
                    EndModAction();
                }
            };
            row.AddArrangedSubview(enabled);

            UIButton remove = CreateButton("Remove", delegate
            {
                UIAlertController alert = UIAlertController.Create("Remove mod?", "Remove " + mod.Name + " from this device?", UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
                alert.AddAction(UIAlertAction.Create("Remove", UIAlertActionStyle.Destructive, async delegate
                {
                    if (!BeginModAction()) return;
                    try
                    {
                        IProgress<string> progress = new Progress<string>(message => SetStatus(message));
                        List<string> alsoDisabled = await IOSModLibrary.RemoveAsync(mod, progress);
                        ShowInstalled();
                        string message = mod.Name + " was removed.";
                        if (alsoDisabled.Count > 0)
                            message += " Disabled mods that require it: " + string.Join(", ", alsoDisabled) + ".";
                        SetStatus(message);
                    }
                    catch (Exception exception)
                    {
                        SetStatus("Could not remove mod: " + exception.Message, true);
                    }
                    finally
                    {
                        EndModAction();
                    }
                }));
                PresentViewController(alert, true, null);
            });
            remove.WidthAnchor.ConstraintEqualTo(86).Active = true;
            row.AddArrangedSubview(remove);
            return row;
        }

        private bool BeginModAction()
        {
            if (modActionInProgress || startingGame) return false;
            modActionInProgress = true;
            View.UserInteractionEnabled = false;
            return true;
        }

        private void EndModAction()
        {
            modActionInProgress = false;
            View.UserInteractionEnabled = true;
        }

        private async void BrowseCatalog(string query)
        {
            currentPage = "browse";
            searchContainer.Hidden = false;
            ClearRows();
            SetStatus("Loading Everest mods...");
            try
            {
                List<CatalogMod> catalog = await IOSModLibrary.SearchCatalogAsync(query);
                if (catalog.Count == 0)
                {
                    AddMessage("No downloadable Everest packages matched that search.");
                    SetStatus("Search by mod name or author.");
                    return;
                }
                foreach (CatalogMod mod in catalog) rows.AddArrangedSubview(CreateCatalogRow(mod));
                SetStatus("Showing " + catalog.Count + " latest Everest package file(s). Downloads are saved on this device.");
            }
            catch (Exception exception)
            {
                AddMessage("The catalog could not be reached. Check your connection and try again.");
                SetStatus(exception.Message, true);
            }
        }

        private UIView CreateCatalogRow(CatalogMod mod)
        {
            UIStackView row = CreateRow();
            UIStackView details = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 4
            };
            details.AddArrangedSubview(CreateLabel(mod.DisplayName, 17, true, UIColor.White));
            string fileSize = mod.Size > 0 ? (mod.Size / 1048576.0).ToString("0.#") + " MB" : "size unknown";
            details.AddArrangedSubview(CreateLabel(mod.Author + "  ·  " + fileSize, 13, false, new UIColor(0.72f, 0.75f, 0.82f, 1f)));
            if (!string.IsNullOrWhiteSpace(mod.Description)) details.AddArrangedSubview(CreateLabel(mod.Description, 13, false, new UIColor(0.72f, 0.75f, 0.82f, 1f)));
            row.AddArrangedSubview(details);

            UIButton install = CreateButton("Install", delegate { });
            install.WidthAnchor.ConstraintEqualTo(110).Active = true;
            install.TouchUpInside += async delegate
            {
                install.Enabled = false;
                await InstallModAsync(mod, install);
            };
            row.AddArrangedSubview(install);
            return row;
        }

        private async Task InstallModAsync(CatalogMod mod, UIButton button)
        {
            if (!BeginModAction())
            {
                if (button != null) button.Enabled = true;
                return;
            }
            try
            {
                IProgress<string> progress = new Progress<string>(message => SetStatus(message));
                string result = await IOSModLibrary.InstallAsync(mod, progress);
                SetStatus(result);
                if (currentPage == "installed") ShowInstalled();
            }
            catch (Exception exception)
            {
                SetStatus("Install failed: " + exception.Message, true);
            }
            finally
            {
                if (button != null) button.Enabled = true;
                EndModAction();
            }
        }

        private async void UpdateMods()
        {
            if (!BeginModAction()) return;
            searchContainer.Hidden = true;
            ClearRows();
            AddMessage("Checking Everest's update list. Each downloaded update is verified before it replaces the installed ZIP.");
            SetStatus("Checking for mod updates...");
            try
            {
                IProgress<string> progress = new Progress<string>(message => SetStatus(message));
                string result = await IOSModLibrary.UpdateAllAsync(progress);
                ShowInstalled();
                SetStatus(result);
            }
            catch (Exception exception)
            {
                SetStatus("Update check failed: " + exception.Message, true);
            }
            finally
            {
                EndModAction();
            }
        }

        private async void StartGame()
        {
            if (!BeginModAction()) return;
            startingGame = true;
            playButton.Enabled = false;
            try
            {
                if (IOSModLibrary.AutoUpdateOnLaunch && IOSModLibrary.GetInstalledMods().Count > 0)
                {
                    try
                    {
                        SetStatus("Checking for mod updates before launch...");
                        IProgress<string> progress = new Progress<string>(message => SetStatus(message));
                        string updateResult = await IOSModLibrary.UpdateAllAsync(progress);
                        SetStatus(updateResult);
                    }
                    catch (Exception updateException)
                    {
                        SetStatus("Update check failed; starting with installed mods. " + updateException.Message, true);
                    }
                }
                IProgress<string> dependencyProgress = new Progress<string>(message => SetStatus(message));
                await IOSModLibrary.ResolveEnabledDependenciesAsync(dependencyProgress);
                IOSModLibrary.PrepareForLaunch();
                SetStatus("Starting Celeste with enabled maps and loose assets...");
                playGame();
            }
            catch (Exception exception)
            {
                SetStatus("Could not prepare enabled mods: " + exception.Message, true);
            }
            finally
            {
                startingGame = false;
                playButton.Enabled = true;
                EndModAction();
            }
        }

        public void GameDidReturn(bool restartRequested)
        {
            if (currentPage == "installed") ShowInstalled();
            if (restartRequested)
            {
                playButton.Enabled = false;
                SetStatus("Dependencies installed. Close and reopen Celeste to load them.");
                UIAlertController alert = UIAlertController.Create(
                    "Restart Celeste",
                    "Everest installed the required files. Close Celeste from the app switcher, then open it again to load the mods.",
                    UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                PresentViewController(alert, true, null);
                return;
            }
            SetStatus("Celeste closed. Change mods or press Play to start again.");
        }

        public void ShowProblem(Exception exception)
        {
            string message = exception == null ? "Unknown startup error." : exception.Message;
            SetStatus("Celeste could not start: " + message, true);
            UIAlertController alert = UIAlertController.Create("Celeste could not start", message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }

        private void ShareDiagnostics()
        {
            string[] paths = Application.GetDiagnosticFilePaths();
            if (paths.Length == 0)
            {
                SetStatus("No diagnostic logs are available yet. Restart Celeste and try again.", true);
                return;
            }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string readmePath = Path.Combine(documents, "celeste-diagnostics-readme.txt");
            string readme = "Celeste iOS diagnostics" + Environment.NewLine +
                "The attached text files contain the startup trace and any managed exception stacks." + Environment.NewLine +
                "If Celeste closed without a stack in these files, also attach the matching iOS .ips report from " +
                "Settings > Privacy & Security > Analytics & Improvements > Analytics Data." + Environment.NewLine +
                "The iOS system report is outside the app's private storage, so the app cannot attach it automatically." +
                Environment.NewLine;
            try { File.WriteAllText(readmePath, readme); }
            catch (Exception exception)
            {
                SetStatus("Could not prepare diagnostics: " + exception.Message, true);
                return;
            }

            NSUrl[] items = paths.Concat(new[] { readmePath })
                .Select(NSUrl.FromFilename)
                .ToArray();
            UIActivityViewController share = new UIActivityViewController(items, null);
            if (share.PopoverPresentationController != null)
            {
                share.PopoverPresentationController.SourceView = shareLogsButton;
                share.PopoverPresentationController.SourceRect = shareLogsButton.Bounds;
            }
            PresentViewController(share, true, null);
        }

        private void AddMessage(string message)
        {
            rows.AddArrangedSubview(CreateLabel(message, 16, false, new UIColor(0.78f, 0.80f, 0.85f, 1f)));
        }

        private UIStackView CreateRow()
        {
            UIStackView row = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Center,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 12
            };
            row.LayoutMargins = new UIEdgeInsets(12, 14, 12, 14);
            row.LayoutMarginsRelativeArrangement = true;
            row.BackgroundColor = new UIColor(0.105f, 0.12f, 0.16f, 1f);
            row.Layer.CornerRadius = 12;
            row.ClipsToBounds = true;
            row.HeightAnchor.ConstraintGreaterThanOrEqualTo(68).Active = true;
            return row;
        }

        private UILabel CreateLabel(string value, nfloat size, bool bold, UIColor color)
        {
            return new UILabel
            {
                Text = value,
                TextColor = color,
                Font = bold ? UIFont.BoldSystemFontOfSize(size) : UIFont.SystemFontOfSize(size),
                Lines = 0,
                LineBreakMode = UILineBreakMode.TailTruncation
            };
        }

        private void SetStatus(string message, bool error = false)
        {
            statusLabel.Text = message;
            statusLabel.TextColor = error ? new UIColor(1f, 0.58f, 0.48f, 1f) : new UIColor(0.72f, 0.75f, 0.82f, 1f);
        }
    }
}
