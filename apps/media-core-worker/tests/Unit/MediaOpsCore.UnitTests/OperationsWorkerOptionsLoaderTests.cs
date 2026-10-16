using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class OperationsWorkerOptionsLoaderTests
{
    [Fact]
    public void Load_should_default_retention_and_disk_threshold_when_file_does_not_exist()
    {
        var options = OperationsWorkerOptionsLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Equal(3, options.AudioRetentionDays);
        Assert.Equal(10, options.MinFreeDiskGb);
    }

    [Fact]
    public void Load_should_return_defaults_when_file_does_not_exist()
    {
        var options = OperationsWorkerOptionsLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        // NormalizeConfiguredPaths deliberately resolves these to absolute paths (so the worker
        // finds its config regardless of the process's current working directory) — only the
        // relative suffix is still meaningful to assert on.
        Assert.EndsWith(Path.Combine("stage", "plugin-profiles.json"), options.PluginProfilesFilePath);
        Assert.Equal("radio,video", options.ContinuousMediaAllowList);
        Assert.Equal(64, options.CaptureMaxDegreeOfParallelism);
        Assert.True(options.EnableStartupValidation);
        Assert.True(options.EnableStartupDiscoveryOnFailedOnly);
        Assert.Equal(12, options.StartupValidationTimeoutSeconds);
        Assert.Equal(10, options.StartupDiscoveryRequestTimeoutSeconds);
    }

    [Fact]
    public void Load_should_default_web_news_to_disabled()
    {
        var options = OperationsWorkerOptionsLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.False(options.WebNewsEnabled);
        Assert.Equal(8, options.WebNewsKeywordsPerQuery);
        Assert.Equal(4, options.WebNewsMaxConcurrency);
        Assert.Equal(20, options.WebNewsRequestTimeoutSeconds);
        Assert.Equal(3, options.WebNewsMaxFailedAttempts);
        Assert.False(options.DailyHeadlinesEnabled);
        Assert.Equal("07:00", options.DailyHeadlinesTime);
    }

    [Fact]
    public void Load_should_read_the_daily_headlines_schedule_and_ignore_a_malformed_time()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-digest-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempPath, """{ "dailyHeadlinesEnabled": true, "dailyHeadlinesTime": "6:30pm" }""");
            var malformed = OperationsWorkerOptionsLoader.Load(tempPath);
            Assert.True(malformed.DailyHeadlinesEnabled);
            Assert.Equal("07:00", malformed.DailyHeadlinesTime);

            File.WriteAllText(tempPath, """{ "dailyHeadlinesTime": "06:30" }""");
            Assert.Equal("06:30", OperationsWorkerOptionsLoader.Load(tempPath).DailyHeadlinesTime);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_apply_web_news_values_from_json_file()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-webnews-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempPath, """
            {
              "webNewsEnabled": true,
              "webNewsKeywordsPerQuery": 5,
              "webNewsMaxConcurrency": 2,
              "webNewsRequestTimeoutSeconds": 25,
              "webNewsMaxFailedAttempts": 5
            }
            """);

            var options = OperationsWorkerOptionsLoader.Load(tempPath);

            Assert.True(options.WebNewsEnabled);
            Assert.Equal(5, options.WebNewsKeywordsPerQuery);
            Assert.Equal(2, options.WebNewsMaxConcurrency);
            Assert.Equal(25, options.WebNewsRequestTimeoutSeconds);
            Assert.Equal(5, options.WebNewsMaxFailedAttempts);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_apply_values_from_json_file()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempPath, """
            {
              "heartbeatIntervalSeconds": 12,
              "discreteWorkerIntervalSeconds": 45,
              "pluginProfilesFilePath": "stage/plugin-profiles.json",
              "continuousMediaAllowList": "radio",
              "captureMaxDegreeOfParallelism": 12,
              "enableCanaryMode": true,
              "canaryPlatformPercent": 40,
              "canaryPlatformMinPercent": 20,
              "canaryPlatformMaxPercent": 90,
              "canaryPlatformAllowList": "javeriana,colmundo",
                            "stageFilesystemRootPath": "stage-evidence-custom",
                            "defaultFlacWindowDurationSeconds": 15,
                            "defaultOpusFlushIntervalSeconds": 20,
                            "defaultOpusRotationIntervalHours": 2,
                            "defaultOpusBitrateKbps": 96,
                            "enableDecoderReconnect": true,
                            "decoderReconnectDelayMaxSeconds": 8,
                            "rtspPreferTcp": true,
                            "enableFlacSilenceChunking": true,
                            "flacSilenceMinChunkSeconds": 21,
                            "flacSilenceMaxChunkSeconds": 31,
                            "flacSilenceHoldMilliseconds": 350,
                            "flacSilenceAnalysisWindowMilliseconds": 25,
                            "flacSilenceAdaptiveThresholdMultiplier": 1.8,
                            "flacSilenceNoiseFloorEmaAlpha": 0.1,
                            "flacSilenceHighPassCutoffHz": 140,
                            "enableStartupValidation": true,
                            "enableStartupDiscoveryOnFailedOnly": true,
                            "startupValidationTimeoutSeconds": 18,
                            "startupDiscoveryRequestTimeoutSeconds": 14,
                            "audioRetentionDays": 7,
                            "minFreeDiskGb": 25
            }
            """);

            var options = OperationsWorkerOptionsLoader.Load(tempPath);

            Assert.Equal(TimeSpan.FromSeconds(45), options.DiscreteWorkerInterval);
            Assert.Equal("radio", options.ContinuousMediaAllowList);
            Assert.Equal(12, options.CaptureMaxDegreeOfParallelism);
            Assert.True(options.EnableCanaryMode);
            Assert.Equal(40, options.CanaryPlatformPercent);
            Assert.Equal(20, options.CanaryPlatformMinPercent);
            Assert.Equal(90, options.CanaryPlatformMaxPercent);
            Assert.Equal("javeriana,colmundo", options.CanaryPlatformAllowList);
            Assert.EndsWith("stage-evidence-custom", options.StageFilesystemRootPath);
            Assert.Equal(15, options.DefaultFlacWindowDurationSeconds);
            Assert.Equal(20, options.DefaultOpusFlushIntervalSeconds);
            Assert.Equal(2, options.DefaultOpusRotationIntervalHours);
            Assert.Equal(96, options.DefaultOpusBitrateKbps);
            Assert.True(options.EnableDecoderReconnect);
            Assert.Equal(8, options.DecoderReconnectDelayMaxSeconds);
            Assert.True(options.RtspPreferTcp);
            Assert.True(options.EnableFlacSilenceChunking);
            Assert.Equal(21, options.FlacSilenceMinChunkSeconds);
            Assert.Equal(31, options.FlacSilenceMaxChunkSeconds);
            Assert.Equal(350, options.FlacSilenceHoldMilliseconds);
            Assert.Equal(25, options.FlacSilenceAnalysisWindowMilliseconds);
            Assert.Equal(1.8, options.FlacSilenceAdaptiveThresholdMultiplier);
            Assert.Equal(0.1, options.FlacSilenceNoiseFloorEmaAlpha);
            Assert.Equal(140, options.FlacSilenceHighPassCutoffHz);
            Assert.True(options.EnableStartupValidation);
            Assert.True(options.EnableStartupDiscoveryOnFailedOnly);
            Assert.Equal(18, options.StartupValidationTimeoutSeconds);
            Assert.Equal(14, options.StartupDiscoveryRequestTimeoutSeconds);
            Assert.Equal(7, options.AudioRetentionDays);
            Assert.Equal(25, options.MinFreeDiskGb);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Load_should_enable_the_email_channel_only_when_smtp_host_and_from_are_set()
    {
        using var env = new EnvironmentVariableScope(
            ("SMTP_HOST", "smtp.example.com"),
            ("SMTP_FROM", "alertas@example.com"),
            ("SMTP_PORT", "2525"),
            ("SMTP_USER", "user"),
            ("SMTP_PASSWORD", "secret"),
            ("SMTP_ENABLE_SSL", "false"));

        var options = OperationsWorkerOptionsLoader.Load();

        Assert.NotNull(options.Smtp);
        Assert.Equal("smtp.example.com", options.Smtp!.Host);
        Assert.Equal("alertas@example.com", options.Smtp.From);
        Assert.Equal(2525, options.Smtp.Port);
        Assert.False(options.Smtp.EnableSsl);
    }

    [Fact]
    public void Load_should_leave_smtp_null_when_the_host_is_missing()
    {
        using var env = new EnvironmentVariableScope(
            ("SMTP_HOST", null),
            ("SMTP_FROM", "alertas@example.com"));

        Assert.Null(OperationsWorkerOptionsLoader.Load().Smtp);
    }

    [Fact]
    public void Load_should_never_let_a_negative_retention_move_the_purge_cutoff_into_the_future()
    {
        // A cutoff after today would put today's own folder under the purge.
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-retention-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, """{ "audioRetentionDays": -3 }""");

            Assert.Equal(0, OperationsWorkerOptionsLoader.Load(tempPath).AudioRetentionDays);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_activate_the_archive_only_with_the_flag_and_the_environment_credentials()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-archive-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, """{ "archiveEnabled": true, "archiveMaxParallelUploads": 4, "archiveFtpAllowInvalidCertificate": true }""");

            using (new EnvironmentVariableScope(
                ("ARCHIVE_FTP_URL", null), ("ARCHIVE_FTP_USER", null), ("ARCHIVE_FTP_PASSWORD", null),
                ("ARCHIVE_RADIO_FTP_URL", null), ("ARCHIVE_TELEVISION_FTP_URL", null), ("ARCHIVE_PRESS_FTP_URL", null)))
            {
                var withoutCredentials = OperationsWorkerOptionsLoader.Load(tempPath);
                Assert.False(withoutCredentials.IsArchiveActive);
                Assert.Equal(4, withoutCredentials.ArchiveMaxParallelUploads);
                Assert.True(withoutCredentials.ArchiveFtpAllowInvalidCertificate);
            }

            using (new EnvironmentVariableScope(
                ("ARCHIVE_FTP_URL", "ftp://archivo.example.com:2121/monitoreo"),
                ("ARCHIVE_FTP_USER", "juan"),
                ("ARCHIVE_FTP_PASSWORD", "secreto")))
            {
                var options = OperationsWorkerOptionsLoader.Load(tempPath);
                Assert.True(options.IsArchiveActive);
                Assert.Equal("juan", options.ArchiveFtp!.User);
                Assert.Empty(options.ArchiveFtpByMedium);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_read_the_local_days_of_television_apart_and_leave_them_unset_otherwise()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-tvdays-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, "{ \"audioRetentionDays\": 31 }");
            var general = OperationsWorkerOptionsLoader.Load(tempPath);
            Assert.Null(general.AudioRetentionDaysTelevision);
            Assert.Equal(31, general.LocalRetentionDaysFor("television"));

            File.WriteAllText(tempPath, "{ \"audioRetentionDays\": 31, \"audioRetentionDaysTelevision\": 0, \"archiveRetentionDaysTelevision\": 1 }");
            var own = OperationsWorkerOptionsLoader.Load(tempPath);
            Assert.Equal(0, own.LocalRetentionDaysFor("television"));
            Assert.Equal(31, own.LocalRetentionDaysFor("radio"));
            Assert.Equal(1, own.ArchiveRetentionDaysTelevision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_read_the_public_address_of_each_medium_and_fall_back_to_the_old_one_for_the_press()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, "{ }");

            using (new EnvironmentVariableScope(
                ("ARCHIVE_RADIO_PUBLIC_BASE_URL", "https://radio.example.org/"),
                ("ARCHIVE_TELEVISION_PUBLIC_BASE_URL", "https://tv.example.com/"),
                ("ARCHIVE_PRESS_PUBLIC_BASE_URL", "https://prensa.example.org/"),
                ("ARCHIVE_PUBLIC_BASE_URL", "https://old.example.org/")))
            {
                var options = OperationsWorkerOptionsLoader.Load(tempPath);

                Assert.Equal("https://radio.example.org/", options.PublicBaseUrlFor("radio"));
                Assert.Equal("https://tv.example.com/", options.PublicBaseUrlFor("television"));
                Assert.Equal("https://prensa.example.org/", options.PublicBaseUrlFor("press"));
            }

            using (new EnvironmentVariableScope(
                ("ARCHIVE_RADIO_PUBLIC_BASE_URL", null), ("ARCHIVE_TELEVISION_PUBLIC_BASE_URL", null), ("ARCHIVE_PRESS_PUBLIC_BASE_URL", null),
                ("ARCHIVE_PUBLIC_BASE_URL", "https://old.example.org/")))
            {
                var options = OperationsWorkerOptionsLoader.Load(tempPath);

                // The single address used before is the press's (it only ever served the PDF of press notes).
                Assert.Equal("https://old.example.org/", options.PublicBaseUrlFor("press"));
                Assert.Null(options.PublicBaseUrlFor("radio"));
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_should_read_an_archive_account_per_medium_and_the_days_each_one_keeps()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-media-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, """{ "archiveEnabled": true, "archiveRetentionDaysRadio": 10, "archiveRetentionDaysTelevision": 5 }""");

            using (new EnvironmentVariableScope(
                ("ARCHIVE_FTP_URL", null), ("ARCHIVE_FTP_USER", null), ("ARCHIVE_FTP_PASSWORD", null),
                ("ARCHIVE_RADIO_FTP_URL", "ftp://archivo.example.com:21/radio"),
                ("ARCHIVE_RADIO_FTP_USER", "radio-user"),
                ("ARCHIVE_RADIO_FTP_PASSWORD", "radio-secret"),
                ("ARCHIVE_TELEVISION_FTP_URL", null), ("ARCHIVE_TELEVISION_FTP_USER", null), ("ARCHIVE_TELEVISION_FTP_PASSWORD", null),
                ("ARCHIVE_PRESS_FTP_URL", "ftp://archivo.example.com:21/prensa"),
                ("ARCHIVE_PRESS_FTP_USER", "press-user"),
                ("ARCHIVE_PRESS_FTP_PASSWORD", "press-secret")))
            {
                var options = OperationsWorkerOptionsLoader.Load(tempPath);

                Assert.True(options.IsArchiveActive);
                Assert.Null(options.ArchiveFtp);
                Assert.Equal("radio-user", options.CredentialsFor("radio")!.User);
                Assert.Equal("press-user", options.CredentialsFor("press")!.User);
                Assert.Null(options.CredentialsFor("television")); // no account of its own and no single one to fall back to
                Assert.Equal(10, options.ArchiveRetentionDaysRadio);
                Assert.Equal(5, options.ArchiveRetentionDaysTelevision);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // Sets environment variables for the duration of a test and restores their previous values
    // (including "was absent") on dispose — keeps FIREBASE_* tests isolated from whatever the
    // real process environment (or another test) happens to have set.
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Key, string? Previous)[] previousValues;

        public EnvironmentVariableScope(params (string Key, string? Value)[] variables)
        {
            previousValues = variables
                .Select(v => (v.Key, Environment.GetEnvironmentVariable(v.Key)))
                .ToArray();

            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, previous) in previousValues)
            {
                Environment.SetEnvironmentVariable(key, previous);
            }
        }
    }
}

