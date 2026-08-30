namespace PassCrate.Core.Help;

public static class HelpCatalog
{
    private static readonly IReadOnlyList<HelpTopic> Topics =
    [
        Topic(
            HelpTopicIds.Welcome,
            HelpCategory.GettingStarted,
            "Welcome",
            "Getting started",
            "Choose whether to create a new vault or restore an encrypted cloud backup.",
            ["welcome", "first launch", "create", "restore", "offline", "tracking", "encrypted"],
            "The Welcome page is shown when this device does not have a local PassCrate vault. Create my vault starts a new vault; Restore from cloud brings back an existing encrypted backup.",
            [
                "Choose Create my vault if this is your first PassCrate vault.",
                "Choose Restore from cloud if another device has already uploaded a PassCrate backup.",
            ],
            "Creating continues to the passphrase setup page. Restoring asks you to connect the cloud account that holds your encrypted backup.",
            "PassCrate works offline after setup, does not track your activity, and encrypts vault data. Cloud backup is optional.",
            Trouble(
                ("Unlock is not shown", "Unlock appears only after a vault has been created or restored on this device."),
                ("You expected an existing vault", "Choose Restore from cloud and connect the same provider and account used on your other device.")),
            [HelpScreenIds.Welcome]),

        Topic(
            HelpTopicIds.CreateVault,
            HelpCategory.GettingStarted,
            "Create Vault",
            "Create your vault",
            "Protect a new vault with one memorable passphrase.",
            ["create", "registration", "passphrase", "16 characters", "validation", "checkbox", "highlight"],
            "This page creates the protected vault stored on your current device. The vault passphrase is required to unlock it and to restore optional cloud backups.",
            [
                "Enter a passphrase containing at least 16 characters.",
                "Enter the same passphrase again in Confirm passphrase.",
                "Read the recovery warning and select the I understand checkbox.",
                "Tap Create my vault.",
            ],
            "When every field is valid, PassCrate creates and unlocks the local vault. Invalid fields are highlighted and show a specific message.",
            "PassCrate cannot retrieve a forgotten passphrase. Use a long, memorable sentence that you do not reuse elsewhere.",
            Trouble(
                ("The passphrase is rejected", "First make it at least 16 characters. After the length is valid, follow any remaining guidance shown below the field."),
                ("The confirmation is highlighted", "Enter exactly the same passphrase in both fields."),
                ("The checkbox is highlighted", "Read the warning and select the checkbox before creating the vault.")),
            [HelpScreenIds.Registration]),

        Topic(
            HelpTopicIds.Unlock,
            HelpCategory.GettingStarted,
            "Unlock",
            "Unlock your vault",
            "Decrypt the vault on this device with your passphrase or enabled biometrics.",
            ["unlock", "passphrase", "fingerprint", "face", "biometric", "incorrect"],
            "The Unlock page appears only when this device has a local vault. Unlocking makes its groups and secrets available for this app session.",
            [
                "Enter your vault passphrase and tap Unlock vault.",
                "If biometric unlock was enabled in Settings, you can instead tap Unlock with biometrics.",
                "If biometrics are cancelled or not recognized, use the vault passphrase.",
            ],
            "A successful unlock opens Home and starts any enabled cloud synchronization. An unsuccessful attempt leaves the vault locked and highlights the passphrase field when appropriate.",
            "Your passphrase is used on this device and is cleared from the input after the attempt. Phone biometrics are handled by the operating system.",
            Trouble(
                ("The passphrase does not work", "Check capitalization and spacing, then enter the same passphrase used when the vault was created or restored."),
                ("The biometric button is missing", "Unlock with the passphrase, then enable Biometric unlock in Settings. Your phone must also have fingerprint or face unlock configured."),
                ("Phone authentication is temporarily unavailable", "Unlock with the vault passphrase or try again after unlocking the phone normally.")),
            [HelpScreenIds.Unlock]),

        Topic(
            HelpTopicIds.UnlockRecovery,
            HelpCategory.GettingStarted,
            "Unlock and Reset",
            "Forgotten passphrase and recovery",
            "Understand what can be restored and when resetting is the only option.",
            ["forgot", "recover", "recovery", "reset", "lost passphrase", "backup"],
            "PassCrate cannot display, retrieve, or replace a forgotten vault passphrase. An encrypted cloud backup also needs the original passphrase.",
            [
                "Try the exact passphrase used to create the vault, including spaces and capitalization.",
                "If you know the passphrase and have a cloud backup, you can restore that backup on a clean device.",
                "If the passphrase is permanently lost, use Reset PassCrate only when you accept losing the local vault.",
                "Confirm RESET and complete the phone's identity check to start again.",
            ],
            "Reset removes the local vault and returns this device to Welcome. Existing encrypted cloud files remain, but they stay unreadable without their original passphrase.",
            "There is no recovery key or hidden back door. Reset requires fingerprint, face, phone PIN, or phone password verification and is permanent.",
            Trouble(
                ("You remember a backup but not its passphrase", "The backup cannot be decrypted. Resetting the device does not change or unlock the cloud file."),
                ("Phone verification fails", "Unlock the phone normally, confirm a screen lock is configured, and try Reset again.")),
            []),

        Topic(
            HelpTopicIds.Home,
            HelpCategory.UsingYourVault,
            "Home",
            "Home and vault overview",
            "See vault status, groups, totals, recent items, and sync conflicts.",
            ["home", "dashboard", "groups", "secrets", "recent", "refresh", "sync", "conflict"],
            "Home is the local overview of your unlocked vault. It shows secret and group totals, your groups, recently updated items, and any conflict that needs review.",
            [
                "Tap Add a secret to save a password, key, or private note.",
                "Tap New group to organize related secrets.",
                "Tap a group or recently updated item to open it.",
                "Tap the conflict message when a cloud merge needs your choice.",
            ],
            "Home reloads local vault information when it appears. There is no pull-to-refresh gesture. Cloud changes arrive through automatic sync or Sync now.",
            "Automatic sync can run after a local change, after unlock, when the app returns to the foreground, when connectivity returns, and about every 15 minutes while unlocked in the foreground.",
            Trouble(
                ("A change from another device is not visible", "Make sure cloud sync is enabled and connected, then open Settings > Cloud sync and tap Sync now."),
                ("A warning says conflicts need review", "Open the warning and choose which saved version to keep or combine.")),
            [HelpScreenIds.Dashboard]),

        Topic(
            HelpTopicIds.Search,
            HelpCategory.UsingYourVault,
            "Search",
            "Search your vault",
            "Find groups and secrets without searching sensitive values.",
            ["search", "find", "name", "group", "field label", "notes", "values"],
            "Search matches group names, secret names, and custom field labels. For privacy, it does not search secret values or private notes.",
            [
                "Open Search from the bottom navigation.",
                "Type part of a group name, secret name, or field label.",
                "Tap a matching group or secret to open it.",
                "Clear the search text to return to the full view.",
            ],
            "Results update as you type. Groups and secrets are shown separately, and a friendly empty state appears when nothing matches.",
            "Avoid putting sensitive values into names or field labels just to make them searchable. Values and notes are intentionally excluded.",
            Trouble(
                ("A password or note is not found", "Search for the secret's name, its group, or a field label such as Username instead."),
                ("No matching secrets appears", "Try fewer words or check the spelling of the name or label.")),
            [HelpScreenIds.Search]),

        Topic(
            HelpTopicIds.GroupDetails,
            HelpCategory.UsingYourVault,
            "Group Details",
            "View and manage a group",
            "Open the secrets in a group, add another secret, rename it, or delete it.",
            ["group", "details", "rename", "delete", "add secret", "empty", "move"],
            "A group keeps related secrets together. Its page shows the group name, icon, color, encrypted-secret count, and every secret currently assigned to it.",
            [
                "Tap a secret to open its protected details.",
                "Tap Add secret to create a secret already assigned to this group.",
                "Use Rename group in the page menu to change its name, icon, or color.",
                "Use Delete group only after reading the confirmation and any instructions for contained secrets.",
            ],
            "An empty group invites you to add its first secret. When deletion requires secrets to be moved or reviewed, PassCrate explains the available action before changing data.",
            "Group names and appearance are encrypted vault data. Deleting a group should never be used as a substitute for reviewing the secrets it contains.",
            Trouble(
                ("The group is empty", "Tap Add secret to create the first item, or edit an existing secret and choose this group."),
                ("The group cannot be deleted", "Review the message and move or remove the secrets that still depend on the group."),
                ("The group cannot be loaded", "Return Home and try again. If cloud sync reports a conflict, review it first.")),
            [HelpScreenIds.GroupDetails]),

        Topic(
            HelpTopicIds.EditGroup,
            HelpCategory.UsingYourVault,
            "Create or Edit Group",
            "Create or edit a group",
            "Choose a clear name, recognizable icon, and friendly named color.",
            ["new group", "edit group", "name", "icon", "color", "colour", "preview", "validation"],
            "The group editor is used for both new groups and existing groups. The preview shows how the selected name, icon, and color will look in the vault.",
            [
                "Enter a group name such as Personal, Work, or Banking.",
                "Choose an icon that makes the group easy to recognize.",
                "Choose a color by its ordinary name; technical hexadecimal codes are not required.",
                "Check the preview and tap Save group.",
            ],
            "A valid group is saved and appears on Home. If the name is missing or invalid, its field is highlighted with a specific message.",
            "Names, icons, and colors help organization but do not change the encryption applied to the group's secrets.",
            Trouble(
                ("The group will not save", "Enter a clear group name and correct the highlighted field."),
                ("The preview looks incomplete", "Choose an icon and named color, and make sure the name is not blank.")),
            [HelpScreenIds.GroupEditor]),

        Topic(
            HelpTopicIds.SecretDetails,
            HelpCategory.UsingYourVault,
            "Secret Details",
            "View, copy, and manage a secret",
            "Reveal only what you need, copy values or notes, edit the item, or delete it.",
            ["secret", "details", "reveal", "hide", "copy", "notes", "clipboard", "edit", "delete"],
            "Secret Details keeps protected values hidden until you choose to reveal or copy them. It also provides editing and deletion actions for the current item.",
            [
                "Tap the reveal control beside a protected field only when you need to read it.",
                "Tap Copy beside a field to place that value on the clipboard.",
                "Use Copy notes when you need the complete private note.",
                "Choose Edit to change fields, notes, or the assigned group.",
                "Choose Delete and confirm only when the secret is no longer needed.",
            ],
            "Copied data is removed automatically after the Clipboard cleanup interval selected in Settings. Editing saves an encrypted update and may schedule cloud sync.",
            "Other applications may read data while it is on the clipboard. Paste only into a trusted destination and avoid leaving sensitive values visible on screen.",
            Trouble(
                ("A copied value disappeared", "PassCrate clears copied values after 15, 30, or 60 seconds according to Settings. Copy it again when ready to paste."),
                ("Notes cannot be copied", "Make sure the secret contains notes, then use Copy notes on the detail page.")),
            [HelpScreenIds.SecretDetails]),

        Topic(
            HelpTopicIds.EditSecret,
            HelpCategory.UsingYourVault,
            "Create or Edit Secret",
            "Create or edit a secret",
            "Save a named item with a group, custom protected fields, and optional private notes.",
            ["new secret", "edit secret", "group", "field", "value", "notes", "save", "validation", "dropdown"],
            "The secret editor supports passwords, keys, account details, recovery codes, and private notes. Custom field names let you describe each protected value clearly.",
            [
                "Choose the group where the secret belongs.",
                "Enter a recognizable secret name.",
                "Add field names such as Username, Password, or Recovery code and enter their values.",
                "Use Add field or remove controls to shape the item, then add optional notes.",
                "Tap Save secret and correct any highlighted inputs if validation appears.",
            ],
            "The encrypted item is saved locally. If cloud sync is enabled, PassCrate schedules an encrypted update shortly afterward.",
            "If the phone locks or PassCrate leaves the foreground, open group selectors and sensitive editor content are dismissed before the Unlock page is shown.",
            Trouble(
                ("The secret will not save", "Correct the highlighted name, group, or field input and try again."),
                ("The group is not listed", "Save or create the required group first, then reopen the secret editor."),
                ("An empty field is not needed", "Remove that field before saving so the secret stays easy to understand.")),
            [HelpScreenIds.SecretEditor]),

        Topic(
            HelpTopicIds.Settings,
            HelpCategory.SettingsAndSecurity,
            "Settings",
            "Settings and security controls",
            "Control unlocking, locking, clipboard cleanup, appearance, cloud access, and reset actions.",
            ["settings", "biometric", "auto-lock", "clipboard", "theme", "light", "dark", "lock now", "sync"],
            "Settings changes how PassCrate behaves on this device. Picker and switch changes are saved immediately unless the app reports a specific error.",
            [
                "Enable Biometric unlock to use an enrolled fingerprint or face, with the vault passphrase as the fallback.",
                "Choose When leaving the app for no foreground inactivity timer, or choose 1, 5, or 15 minutes to lock after foreground inactivity.",
                "Choose whether copied values are cleared after 15, 30, or 60 seconds.",
                "Choose System, Light, or Dark theme; the appearance applies across the application.",
                "Open Cloud sync or Review conflicts when backup status needs attention.",
                "Tap Lock vault now to remove the active key, clear the clipboard, pause sync, and return to Unlock.",
            ],
            "Setting changes take effect immediately. Reset PassCrate is separate in the Danger Zone and requires both typed confirmation and phone verification.",
            "PassCrate always locks when it leaves the foreground or the phone locks. Auto-lock choices control only the additional inactivity timer while the app remains visible.",
            Trouble(
                ("Biometric unlock cannot be enabled", "Configure fingerprint or face unlock in the phone's settings, unlock PassCrate with the passphrase, and try again."),
                ("The theme looks inconsistent", "Leave the picker selected and navigate to another page. If a page still uses the old appearance, close and reopen PassCrate."),
                ("You are asked to unlock after switching apps", "This is expected: PassCrate locks whenever it leaves the foreground.")),
            [HelpScreenIds.Settings]),

        Topic(
            HelpTopicIds.ChangePassphrase,
            HelpCategory.SettingsAndSecurity,
            "Change Passphrase",
            "Change your vault passphrase",
            "Replace the passphrase without changing your saved groups and secrets.",
            ["change passphrase", "current", "new", "confirm", "16 characters", "cloud backup"],
            "Changing the passphrase updates how this device unlocks the vault and how future encrypted cloud recovery is protected. It does not rename, remove, or recreate your saved items.",
            [
                "Enter the current vault passphrase.",
                "Enter a new passphrase containing at least 16 characters.",
                "Enter the new passphrase again exactly.",
                "Tap Change vault passphrase and wait for confirmation.",
            ],
            "After success, use the new passphrase for future unlocks and future cloud restoration. Existing groups and secrets remain the same.",
            "Do not forget the new passphrase. Length is validated first; after it reaches 16 characters, follow any remaining guidance on the highlighted field.",
            Trouble(
                ("The current passphrase is rejected", "Enter the passphrase that currently unlocks this device, including the same spaces and capitalization."),
                ("The new fields remain highlighted", "Make the new passphrase at least 16 characters and enter it identically in both new-passphrase fields.")),
            [HelpScreenIds.ChangePassword]),

        Topic(
            HelpTopicIds.Reset,
            HelpCategory.SettingsAndSecurity,
            "Reset PassCrate",
            "Reset PassCrate and start again",
            "Permanently erase the local vault after explicit confirmation and phone-owner verification.",
            ["reset", "erase", "delete", "phone verification", "pin", "fingerprint", "face", "cloud files"],
            "Reset is the final option when you intentionally want to erase the vault saved on this device, including its groups, secrets, unlock information, and connected cloud sign-ins.",
            [
                "Read the complete deletion warning.",
                "Type RESET exactly in the confirmation field.",
                "Tap Verify identity and reset.",
                "Complete Android or iOS fingerprint, face, PIN, or phone-password verification.",
            ],
            "A successful reset removes local PassCrate data and returns to Welcome. Existing encrypted cloud backup files are kept unless you deleted them separately.",
            "Reset cannot be undone and does not reveal an unknown backup passphrase. PassCrate never receives the fingerprint, face, PIN, or phone password used by the operating system.",
            Trouble(
                ("The reset button does not continue", "Type RESET using capital letters and correct the highlighted confirmation field."),
                ("Phone verification is unavailable", "Make sure the phone has a secure screen lock, unlock it normally, and try again."),
                ("You want cloud files deleted too", "While you still know the vault passphrase, use the deletion controls in Cloud sync before resetting the local app.")),
            [HelpScreenIds.Reset]),

        Topic(
            HelpTopicIds.Restore,
            HelpCategory.CloudBackupAndSync,
            "Restore from Cloud",
            "Restore an encrypted cloud backup",
            "Connect the original cloud account, find a backup, and decrypt it with its passphrase.",
            ["restore", "backup", "Google Drive", "Dropbox", "connect", "find backups", "no backup", "passphrase"],
            "Restore is for a new or reset device when a PassCrate backup already exists in Google Drive or Dropbox. The cloud service stores encrypted backup data.",
            [
                "Choose Google Drive or Dropbox.",
                "Tap the highlighted Connect and find backups button and authorize the same account used on the other device.",
                "When backups are found, choose the correct one from the list.",
                "Enter the original vault passphrase that protected that backup.",
                "Tap Restore my vault.",
            ],
            "The passphrase field appears only after at least one backup is available. A successful restore creates the local vault and opens it on this device.",
            "PassCrate cannot decrypt a backup without its original passphrase. Account authorization gives access only through the selected cloud provider's approved flow.",
            Trouble(
                ("No backups found is shown in red", "Check the provider, cloud account, internet connection, and whether the other device completed at least one upload."),
                ("The backup passphrase is rejected", "Use the exact passphrase in effect when that backup was created."),
                ("The passphrase field is missing", "First connect and find at least one backup, then select it.")),
            [HelpScreenIds.CloudRestore]),

        Topic(
            HelpTopicIds.CloudSync,
            HelpCategory.CloudBackupAndSync,
            "Cloud Sync",
            "Encrypted cloud backup and sync",
            "Connect Google Drive or Dropbox, synchronize safely, or manage encrypted cloud files.",
            ["cloud", "sync", "Google Drive", "Dropbox", "Wi-Fi", "sync now", "disconnect", "delete cloud", "reconnect"],
            "Cloud sync keeps an encrypted copy of the vault in your selected Google Drive or Dropbox account. The page shows connection status, conflicts, provider, and the last successful sync.",
            [
                "Choose a cloud service, re-enter the vault passphrase, and tap Connect and turn on cloud sync.",
                "Use Sync now when you want to check for another device's changes immediately.",
                "Enable Wi-Fi only to avoid synchronization over cellular data.",
                "Use Reconnect if account authorization has expired.",
                "Use Disconnect to stop sync while keeping cloud files.",
                "Use the Danger Zone only to delete the current cloud vault or all PassCrate data in the connected account, following the exact confirmations shown.",
            ],
            "Automatic sync runs shortly after local changes, after unlock, when the app returns to the foreground, when connectivity returns, and approximately every 15 minutes while foregrounded and unlocked. Real-time push sync is not currently used.",
            "Vault data is encrypted before upload, and the backup passphrase is not saved. Deleting cloud data does not delete the local vault; disconnecting does not delete cloud files.",
            Trouble(
                ("Sync is waiting", "Check the connection, Wi-Fi-only setting, and account authorization, then tap Sync now."),
                ("Reconnect is shown", "Tap Reconnect and complete the provider sign-in again."),
                ("Conflicts need review", "Open Review conflicts and resolve each item before expecting a clean status.")),
            [HelpScreenIds.CloudSync]),

        Topic(
            HelpTopicIds.MultipleDevices,
            HelpCategory.CloudBackupAndSync,
            "Cloud Sync",
            "Use PassCrate on multiple devices",
            "Connect the same encrypted vault on each device and understand when changes arrive.",
            ["multiple devices", "second phone", "same account", "same passphrase", "refresh", "sync now", "offline edits"],
            "Multiple-device use depends on the same cloud provider, the same cloud account, and the passphrase that decrypts the shared encrypted backup.",
            [
                "Enable cloud sync and complete an upload on the first device.",
                "On another device, choose Restore from cloud and connect the same provider and account.",
                "Choose the backup and enter its original vault passphrase.",
                "Keep cloud sync connected on both devices.",
                "Use Sync now before making an urgent edit when another device may have changed the same item.",
            ],
            "Changes normally arrive after unlock, foreground return, restored connectivity, periodic foreground sync, or Sync now. They are not pushed instantly in real time.",
            "Avoid changing the same item on multiple offline devices. PassCrate preserves conflicting versions for review instead of silently discarding one.",
            Trouble(
                ("The second device is out of date", "Bring both devices online, open Cloud sync, and tap Sync now on the device that needs the latest changes."),
                ("Two different versions appear", "Open Conflict Review and choose one version, combine them, or keep both.")),
            []),

        Topic(
            HelpTopicIds.Conflicts,
            HelpCategory.CloudBackupAndSync,
            "Conflict Review",
            "Review cloud sync conflicts",
            "Choose what to keep when different devices changed the same encrypted item.",
            ["conflict", "revision", "merge", "keep both", "deleted group", "reveal", "combine"],
            "A conflict is created when PassCrate cannot safely choose between versions from different devices, such as simultaneous offline edits or a deletion that competes with another change.",
            [
                "Choose an item under Items needing your choice.",
                "Select saved revisions to compare them; reveal sensitive values only when required.",
                "Choose Use the selected version, or edit the combined details and choose Use the combined details above.",
                "Choose Keep both versions when both records should remain.",
                "For deletions, choose Keep it deleted, bring a deleted group back, or move its secrets before deleting the group as offered.",
            ],
            "After a choice is saved, the conflict is resolved locally and the result is included in a later cloud synchronization.",
            "Sensitive values remain hidden by default and should be revealed only while actively comparing the conflict.",
            Trouble(
                ("You are unsure which version is newest", "Compare the displayed revision information and content. Keep both if losing either version would be risky."),
                ("A group cannot stay deleted", "Move the secrets that still belong to it, or restore the group, using the actions shown."),
                ("The conflict returns", "Complete Sync now on connected devices and avoid editing the same item until they are up to date.")),
            [HelpScreenIds.ConflictReview]),

        Topic(
            HelpTopicIds.OfflineSecurity,
            HelpCategory.SettingsAndSecurity,
            "App Security",
            "Offline use and device security",
            "Understand what works offline and why PassCrate locks when the phone or app is left.",
            ["offline", "background", "phone lock", "screen lock", "selector", "menu", "security", "unlock again"],
            "Creating, viewing, searching, and editing the local vault work without internet. Cloud authorization, upload, download, and cross-device updates wait for connectivity.",
            [
                "Use the vault normally while offline; local changes remain encrypted on the device.",
                "Reconnect to the internet and allow automatic sync, or use Sync now when appropriate.",
                "Expect PassCrate to lock whenever the phone locks or the app leaves the foreground.",
                "Unlock again with the passphrase or enabled biometrics when you return.",
            ],
            "When PassCrate deactivates, it clears sensitive input and clipboard data, dismisses open selectors, removes the active vault key, pauses sync, and navigates to Unlock.",
            "No group selector, secret field, note, or other inside-app content should remain visible over the locked vault. Requiring unlock after app switching is intentional.",
            Trouble(
                ("You are asked to unlock again after switching apps", "This is the expected app-background security behavior and cannot be disabled by the inactivity setting."),
                ("Cloud status does not change while offline", "Local work continues, but cloud work waits until an allowed connection returns."),
                ("Vault content remains visible after the phone locks", "Leave the app locked and restart it. Treat this as a security defect and do not continue entering secrets until it is corrected.")),
            []),
    ];

    public static IReadOnlyList<HelpTopic> All => Topics;

    public static IReadOnlyList<HelpTopic> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Topics;
        }

        var terms = query.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Topics
            .Where(topic =>
            {
                var searchableText = BuildSearchableText(topic);
                return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
    }

    public static HelpTopic? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Topics.FirstOrDefault(topic => string.Equals(topic.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string GetCategoryTitle(HelpCategory category) => category switch
    {
        HelpCategory.GettingStarted => "Getting Started",
        HelpCategory.UsingYourVault => "Using Your Vault",
        HelpCategory.SettingsAndSecurity => "Settings & Security",
        HelpCategory.CloudBackupAndSync => "Cloud Backup & Sync",
        _ => category.ToString(),
    };

    private static HelpTopic Topic(
        string id,
        HelpCategory category,
        string pageName,
        string title,
        string summary,
        IReadOnlyList<string> keywords,
        string purpose,
        IReadOnlyList<string> steps,
        string outcome,
        string security,
        IReadOnlyList<HelpTroubleshootingItem> troubleshooting,
        IReadOnlyList<string> coveredScreens) => new(
            id,
            category,
            pageName,
            title,
            summary,
            keywords,
            [
                Section("What this page is for", purpose),
                Section("How to use it", "Follow these steps:", steps: steps),
                Section("What happens next", outcome, HelpNoteType.Tip),
                Section("Security or data notes", security, HelpNoteType.Security),
                Section(
                    "Common problems and solutions",
                    "Try the guidance below before repeating the action.",
                    HelpNoteType.Warning,
                    troubleshooting: troubleshooting),
            ],
            coveredScreens);

    private static HelpSection Section(
        string heading,
        string body,
        HelpNoteType noteType = HelpNoteType.Normal,
        IReadOnlyList<string>? steps = null,
        IReadOnlyList<HelpTroubleshootingItem>? troubleshooting = null) => new(
            heading,
            body,
            noteType,
            steps?.Select((text, index) => new HelpStep(index + 1, text)).ToArray() ?? [],
            troubleshooting ?? []);

    private static IReadOnlyList<HelpTroubleshootingItem> Trouble(
        params (string Problem, string Solution)[] entries) =>
        entries.Select(entry => new HelpTroubleshootingItem(entry.Problem, entry.Solution)).ToArray();

    private static string BuildSearchableText(HelpTopic topic) => string.Join(
        ' ',
        new[]
        {
            topic.PageName,
            topic.Title,
            topic.Summary,
            GetCategoryTitle(topic.Category),
            string.Join(' ', topic.Keywords),
            string.Join(' ', topic.Sections.Select(section => string.Join(
                ' ',
                new[]
                {
                    section.Heading,
                    section.Body,
                    string.Join(' ', section.Steps.Select(step => step.Text)),
                    string.Join(' ', section.Troubleshooting.Select(item => $"{item.Problem} {item.Solution}")),
                }))),
        });
}
