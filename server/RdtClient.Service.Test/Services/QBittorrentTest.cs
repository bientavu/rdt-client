using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

public class QBittorrentTest
{
    private readonly Mock<Authentication> _authenticationMock;
    private readonly Mock<ILogger<QBittorrent>> _loggerMock;
    private readonly QBittorrent _qBittorrent;
    private readonly TestSettings _settings;
    private readonly TorrentRunnerState _runnerState;
    private readonly Mock<Torrents> _torrentsMock;

    public QBittorrentTest()
    {
        _loggerMock = new();
        _settings = new();
        _runnerState = new();
        _torrentsMock = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, _settings, _runnerState);
        _authenticationMock = new(null!, null!, null!);

        _qBittorrent = new(_loggerMock.Object, _settings, _authenticationMock.Object, _torrentsMock.Object, null!, _runnerState);
    }

    [Fact]
    public async Task TorrentInfo_ShouldOnlyReturnTorrents()
    {
        // Arrange
        var allTorrents = new List<Torrent>
        {
            new()
            {
                TorrentId = Guid.NewGuid(),
                Hash = "hash1",
                RdName = "Torrent 1",
                Type = DownloadType.Torrent
            },
            new()
            {
                TorrentId = Guid.NewGuid(),
                Hash = "hash2",
                RdName = "NZB 1",
                Type = DownloadType.Nzb
            }
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync(allTorrents);

        // Act
        var result = await _qBittorrent.TorrentInfo();

        // Assert
        Assert.Single(result);
        Assert.Equal("hash1", result[0].Hash);
        Assert.Equal("Torrent 1", result[0].Name);
    }

    [Fact]
    public async Task TorrentInfo_ShouldReport100Percent_WhenDownloadIsComplete()
    {
        // Arrange
        var downloadId = Guid.NewGuid();
        var torrentId = Guid.NewGuid();

        var allTorrents = new List<Torrent>
        {
            new()
            {
                TorrentId = torrentId,
                Hash = "hash1",
                RdName = "Torrent 1",
                RdProgress = 100, // Real-Debrid is 100%
                Type = DownloadType.Torrent,
                Downloads = new List<Download>
                {
                    new()
                    {
                        DownloadId = downloadId,
                        TorrentId = torrentId
                    }
                }
            }
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync(allTorrents);

        // Local download is also 100%
        _torrentsMock.Setup(m => m.GetDownloadStats(downloadId)).Returns((0, 1000, 1000));

        // Act
        var result = await _qBittorrent.TorrentInfo();

        // Assert
        Assert.Single(result);
        Assert.Equal(1.0f, result[0].Progress);
    }

    [Fact]
    public async Task TorrentInfo_ShouldReport0Percent_WhenNothingDownloadedLocally()
    {
        var allTorrents = new List<Torrent>
        {
            new()
            {
                TorrentId = Guid.NewGuid(),
                Hash = "hash1",
                RdName = "Torrent 1",
                RdProgress = 100, // cached on Real-Debrid, local download not started
                Type = DownloadType.Torrent
            }
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync(allTorrents);

        var result = await _qBittorrent.TorrentInfo();

        Assert.Equal(0f, result[0].Progress);
    }

    [Fact]
    public async Task TorrentInfo_ShouldCountFiles_ForSeasonPack()
    {
        var torrentId = Guid.NewGuid();
        var finished = new Download { DownloadId = Guid.NewGuid(), TorrentId = torrentId, DownloadStarted = DateTimeOffset.UtcNow.AddMinutes(-2), DownloadFinished = DateTimeOffset.UtcNow };
        var active = new Download { DownloadId = Guid.NewGuid(), TorrentId = torrentId, DownloadStarted = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var queued = new Download { DownloadId = Guid.NewGuid(), TorrentId = torrentId };
        var queued2 = new Download { DownloadId = Guid.NewGuid(), TorrentId = torrentId };

        var allTorrents = new List<Torrent>
        {
            new()
            {
                TorrentId = torrentId,
                Hash = "hash1",
                RdName = "Show S01",
                RdProgress = 100,
                Type = DownloadType.Torrent,
                Downloads = new List<Download> { finished, active, queued, queued2 }
            }
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync(allTorrents);

        // Only the active download has stats; finished and queued ones are (0, 0, 0).
        _torrentsMock.Setup(m => m.GetDownloadStats(It.IsAny<Guid>())).Returns((0, 0, 0));
        _torrentsMock.Setup(m => m.GetDownloadStats(active.DownloadId)).Returns((500, 1000, 500));

        var result = await _qBittorrent.TorrentInfo();

        // (1 finished + 0.5 active + 0 + 0) / 4 files
        Assert.Equal(0.375f, result[0].Progress);
        Assert.Equal(500, result[0].Dlspeed);
        // 120 s since the first LOCAL start at 37.5% => 320 s total, ~200 s left.
        Assert.InRange(result[0].Eta!.Value, 190, 210);
    }

    [Fact]
    public async Task TorrentInfo_ShouldReportLocalProgressOnly_WhenRDIs100AndLocalIs80()
    {
        // Arrange
        var downloadId = Guid.NewGuid();
        var torrentId = Guid.NewGuid();

        var allTorrents = new List<Torrent>
        {
            new()
            {
                TorrentId = torrentId,
                Hash = "hash1",
                RdName = "Torrent 1",
                RdProgress = 100, // Real-Debrid is 100%
                Type = DownloadType.Torrent,
                Downloads = new List<Download>
                {
                    new()
                    {
                        DownloadId = downloadId,
                        TorrentId = torrentId
                    }
                }
            }
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync(allTorrents);

        // Local download is 80%
        _torrentsMock.Setup(m => m.GetDownloadStats(downloadId)).Returns((0, 1000, 800));

        // Act
        var result = await _qBittorrent.TorrentInfo();

        // Assert
        Assert.Single(result);

        // Custom (bientavu): local progress only, no longer (1.0 + 0.8) / 2 = 0.9
        Assert.Equal(0.8f, result[0].Progress);
    }

    [Fact]
    public async Task TorrentFileContents_ShouldReturnAllFiles_WithIndexesAndPriority()
    {
        // Arrange
        var torrent = new Torrent
        {
            Hash = "hash1",
            Type = DownloadType.Torrent,
            RdFiles = JsonSerializer.Serialize(new List<DebridClientFile>
            {
                new()
                {
                    Id = 1,
                    Path = "good.mkv",
                    Bytes = 1000,
                    Selected = true
                },
                new()
                {
                    Id = 2,
                    Path = "dangerous.exe",
                    Bytes = 10,
                    Selected = false
                }
            })
        };

        _torrentsMock.Setup(m => m.GetByHash("hash1")).ReturnsAsync(torrent);

        // Act
        var result = await _qBittorrent.TorrentFileContents("hash1");

        // Assert
        Assert.NotNull(result);

        Assert.Collection(result!,
                          first =>
                          {
                              Assert.Equal(0, first.Index);
                              Assert.Equal("good.mkv", first.Name);
                              Assert.Equal(1, first.Priority);
                          },
                          second =>
                          {
                              Assert.Equal(1, second.Index);
                              Assert.Equal("dangerous.exe", second.Name);
                              Assert.Equal(0, second.Priority);
                          });
    }

    [Fact]
    public async Task TorrentProperties_ShouldExposePublicTorrentFlag()
    {
        // Arrange
        var torrent = new Torrent
        {
            Hash = "hash1",
            Type = DownloadType.Torrent,
            Added = DateTimeOffset.UtcNow
        };

        _torrentsMock.Setup(m => m.GetByHash("hash1")).ReturnsAsync(torrent);

        // Act
        var result = await _qBittorrent.TorrentProperties("hash1");

        // Assert
        Assert.NotNull(result);
        Assert.False(result!.IsPrivate);
    }

    [Fact]
    public async Task TorrentInfo_ShouldUseSelectedTopLevelFileName_WhenRootFileAddsOnlyTheExtension()
    {
        // Arrange
        var previousMappedPath = _settings.Current.DownloadClient.MappedPath;
        _settings.Current.DownloadClient.MappedPath = "/data/downloads";

        try
        {
            var torrentRootName = "Sample.Show.S01E01.1080p.WEB-DL-GROUP";
            var selectedFileName = $"{torrentRootName}.mkv";

            var torrent = new Torrent
            {
                Hash = "hash1",
                Category = "tv",
                Completed = DateTimeOffset.UtcNow,
                RdName = torrentRootName,
                RdFiles = JsonSerializer.Serialize(new List<DebridClientFile>
                {
                    new()
                    {
                        Id = 1,
                        Path = $"/{selectedFileName}",
                        Bytes = 1000,
                        Selected = true
                    },
                    new()
                    {
                        Id = 2,
                        Path = $"/{torrentRootName}.nfo",
                        Bytes = 10,
                        Selected = false
                    }
                }),
                Type = DownloadType.Torrent
            };

            _torrentsMock.Setup(m => m.Get())
                         .ReturnsAsync(new List<Torrent>
                         {
                             torrent
                         });

            // Act
            var result = await _qBittorrent.TorrentInfo();

            // Assert
            Assert.Single(result);
            Assert.Equal(Path.Combine("/data/downloads", "tv", selectedFileName) + Path.DirectorySeparatorChar, result[0].ContentPath);
            Assert.Equal(torrentRootName, result[0].Name);
        }
        finally
        {
            _settings.Current.DownloadClient.MappedPath = previousMappedPath;
        }
    }

    [Fact]
    public async Task TorrentInfo_ShouldUseExistingCompletedContentRoot_WhenSingleFileDirectoryDiffersFromRdName()
    {
        var previousMappedPath = _settings.Current.DownloadClient.MappedPath;
        var mappedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _settings.Current.DownloadClient.MappedPath = mappedPath;

        try
        {
            var category = "tv";
            var selectedFileName = "Sample.Show.S01E02.1080p.WEB-DL-GROUP.mkv";
            var contentRoot = Path.Combine(mappedPath, category, selectedFileName);
            Directory.CreateDirectory(contentRoot);
            await File.WriteAllTextAsync(Path.Combine(contentRoot, selectedFileName), "test");

            var torrent = new Torrent
            {
                Hash = "hash1",
                Category = category,
                Completed = DateTimeOffset.UtcNow,
                Downloads = new List<Download>
                {
                    new()
                    {
                        DownloadId = Guid.NewGuid(),
                        FileName = selectedFileName,
                        Link = "https://fake.url/Sample.Show.S01E02.1080p.WEB-DL-GROUP.mkv",
                        Completed = DateTimeOffset.UtcNow
                    }
                },
                RdName = "tracker.example    -    Sample.Show.S01E02.1080p.WEB-DL-GROUP",
                RdFiles = JsonSerializer.Serialize(new List<DebridClientFile>
                {
                    new()
                    {
                        Id = 1,
                        Path = $"/{selectedFileName}",
                        Bytes = 1000,
                        Selected = true
                    }
                }),
                Type = DownloadType.Torrent
            };

            _torrentsMock.Setup(m => m.Get())
                         .ReturnsAsync(new List<Torrent>
                         {
                             torrent
                         });

            var result = await _qBittorrent.TorrentInfo();

            Assert.Single(result);
            Assert.Equal(contentRoot + Path.DirectorySeparatorChar, result[0].ContentPath);
        }
        finally
        {
            _settings.Current.DownloadClient.MappedPath = previousMappedPath;

            if (Directory.Exists(mappedPath))
            {
                Directory.Delete(mappedPath, true);
            }
        }
    }

    [Fact]
    public async Task TorrentInfo_ShouldUseExistingCompletedContentRoot_WhenSelectedFileIsNestedUnderDifferentRoot()
    {
        var previousMappedPath = _settings.Current.DownloadClient.MappedPath;
        var mappedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _settings.Current.DownloadClient.MappedPath = mappedPath;

        try
        {
            var category = "tv";
            var rootDirectoryName = "tracker.example - Sample.Show.S01E03.1080p.x265-GROUP.mkv";
            var nestedDirectoryName = "Sample.Show.S01E03.1080p.x265-GROUP";
            var fileName = "Sample.Show.S01E03.1080p.x265-GROUP.mkv";
            var contentRoot = Path.Combine(mappedPath, category, rootDirectoryName);
            var nestedDirectory = Path.Combine(contentRoot, nestedDirectoryName);
            Directory.CreateDirectory(nestedDirectory);
            await File.WriteAllTextAsync(Path.Combine(nestedDirectory, fileName), "test");

            var torrent = new Torrent
            {
                Hash = "hash1",
                Category = category,
                Completed = DateTimeOffset.UtcNow,
                Downloads = new List<Download>
                {
                    new()
                    {
                        DownloadId = Guid.NewGuid(),
                        FileName = fileName,
                        Link = "https://fake.url/Sample.Show.S01E03.1080p.x265-GROUP.mkv",
                        Completed = DateTimeOffset.UtcNow
                    }
                },
                RdName = "tracker.example    -    Sample.Show.S01E03.1080p.x265-GROUP",
                RdFiles = JsonSerializer.Serialize(new List<DebridClientFile>
                {
                    new()
                    {
                        Id = 1,
                        Path = $"/{nestedDirectoryName}/{fileName}",
                        Bytes = 1000,
                        Selected = true
                    }
                }),
                Type = DownloadType.Torrent
            };

            _torrentsMock.Setup(m => m.Get())
                         .ReturnsAsync(new List<Torrent>
                         {
                             torrent
                         });

            var result = await _qBittorrent.TorrentInfo();

            Assert.Single(result);
            Assert.Equal(contentRoot + Path.DirectorySeparatorChar, result[0].ContentPath);
        }
        finally
        {
            _settings.Current.DownloadClient.MappedPath = previousMappedPath;

            if (Directory.Exists(mappedPath))
            {
                Directory.Delete(mappedPath, true);
            }
        }
    }

    [Fact]
    public async Task TorrentInfo_ShouldKeepTorBoxRootNameExtension_WhenTorrentIsNamedAfterItsOnlyFile()
    {
        _settings.Current.DownloadClient.MappedPath = "/data/downloads";

        var torrentRootName = "Sample.Movie.2024.1080p-GROUP.mkv";

        var torrent = new Torrent
        {
            Hash = "hash1",
            Category = "movies",
            ClientKind = Provider.TorBox,
            Completed = DateTimeOffset.UtcNow,
            RdName = torrentRootName,
            RdFiles = JsonSerializer.Serialize(new List<DebridClientFile>
            {
                new()
                {
                    Id = 1,
                    Path = torrentRootName,
                    Bytes = 1000,
                    Selected = true
                }
            }),
            Type = DownloadType.Torrent
        };

        _torrentsMock.Setup(m => m.Get()).ReturnsAsync([torrent]);

        var result = await _qBittorrent.TorrentInfo();

        Assert.Single(result);
        Assert.Equal(Path.Combine("/data/downloads", "movies", torrentRootName) + Path.DirectorySeparatorChar, result[0].ContentPath);
    }

}
