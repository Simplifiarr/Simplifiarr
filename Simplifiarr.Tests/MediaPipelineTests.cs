using System.IO.Compression;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class MediaTypeDefinitionTests : TempFolderTest
{
    private static string Relative(LibraryTarget target, string libraryPath) =>
        Path.GetRelativePath(libraryPath, target.TargetFilePath);

    [TestMethod]
    public void There_is_one_definition_per_type_and_each_has_its_own_cache_file()
    {
        CollectionAssert.AreEquivalent(
            new[] { MediaType.Book, MediaType.Audiobook, MediaType.Music, MediaType.Comic },
            MediaTypeDefinitions.All.Select(definition => definition.MediaType).ToList());

        Assert.HasCount(
            MediaTypeDefinitions.All.Count,
            MediaTypeDefinitions.All.Select(definition => definition.CacheFileName).Distinct().ToList());
    }

    [TestMethod]
    public void Library_files_are_matched_to_the_right_definition()
    {
        Assert.AreSame(MediaTypeDefinitions.Book, MediaTypeDefinitions.ForFile(FileType.Book, null));
        Assert.AreSame(MediaTypeDefinitions.Comic, MediaTypeDefinitions.ForFile(FileType.Comic, null));
        Assert.AreSame(MediaTypeDefinitions.Music, MediaTypeDefinitions.ForFile(FileType.Audio, MediaType.Music));
        Assert.AreSame(MediaTypeDefinitions.Audiobook, MediaTypeDefinitions.ForFile(FileType.Audio, MediaType.Audiobook));
        Assert.AreSame(MediaTypeDefinitions.Audiobook, MediaTypeDefinitions.ForFile(FileType.Audio, null));
        Assert.IsNull(MediaTypeDefinitions.ForFile(FileType.Video, null));
    }

    [TestMethod]
    public void Only_books_are_picked_out_of_the_shared_download_folder()
    {
        var profile = new DriveProfile
        {
            DownloadPath = @"D:\dl",
            AudiobookDownloadPath = @"D:\dl-audiobooks",
            MusicDownloadPath = @"D:\dl-music",
            ComicDownloadPath = @"D:\dl-comics"
        };

        Assert.AreEqual(@"D:\dl", MediaTypeDefinitions.Book.GetDownloadPath(profile));
        Assert.AreEqual(@"D:\dl-audiobooks", MediaTypeDefinitions.Audiobook.GetDownloadPath(profile));
        Assert.AreEqual(@"D:\dl-music", MediaTypeDefinitions.Music.GetDownloadPath(profile));
        Assert.AreEqual(@"D:\dl-comics", MediaTypeDefinitions.Comic.GetDownloadPath(profile));
    }

    [TestMethod]
    public void A_file_with_no_tags_is_read_as_author_dash_title()
    {
        var file = WriteFile("Jane.Author - The_Great.Book.epub", "not a real epub");

        var metadata = MediaTypeDefinitions.Book.ReadMetadata(file);

        Assert.AreEqual("The Great Book", metadata.Title);
        Assert.AreEqual("Jane Author", metadata.Author);
        Assert.IsFalse(metadata.FromEmbeddedTags);
    }

    [TestMethod]
    public void A_file_name_with_no_dash_is_all_title()
    {
        var metadata =
            MediaTypeDefinitions.Audiobook.ReadMetadata(
                WriteFile("Some_Audiobook.m4b", "x"));

        Assert.AreEqual("Some Audiobook", metadata.Title);
        Assert.IsNull(metadata.Author);
    }

    [TestMethod]
    public void Books_and_audiobooks_go_in_a_folder_named_after_the_title()
    {
        var library = Path.Combine(Folder, "books");
        var metadata = new MediaFileMetadata { Title = "Dune: Messiah", Author = "Frank Herbert" };

        var target =
            MediaTypeDefinitions.Book.ResolveTarget(metadata, "epub", library, "orig.epub", false);

        // The colon can't be in a Windows file name.
        Assert.AreEqual(@"Dune Messiah\Dune Messiah.epub", Relative(target, library));

        Assert.AreEqual(
            @"Dune Messiah\Dune Messiah.m4b",
            Relative(
                MediaTypeDefinitions.Audiobook.ResolveTarget(metadata, ".m4b", library, null, false),
                library));
    }

    [TestMethod]
    public void The_original_file_name_can_be_kept()
    {
        var library = Path.Combine(Folder, "books");

        var target =
            MediaTypeDefinitions.Book.ResolveTarget(
                new MediaFileMetadata { Title = "Dune" },
                "epub",
                library,
                "dune.retail.epub",
                preserveOriginalFileName: true);

        Assert.AreEqual(@"Dune\dune.retail.epub", Relative(target, library));
    }

    [TestMethod]
    public void Music_goes_under_artist_then_album_with_the_track_number_first()
    {
        var library = Path.Combine(Folder, "music");

        var full =
            MediaTypeDefinitions.Music.ResolveTarget(
                new MediaFileMetadata { Title = "Song", Author = "Artist", Album = "Album", TrackNumber = 3 },
                "flac",
                library,
                null,
                false);

        Assert.AreEqual(@"Artist\Album\03 - Song.flac", Relative(full, library));

        var bare =
            MediaTypeDefinitions.Music.ResolveTarget(
                new MediaFileMetadata { Title = "Song" },
                "mp3",
                library,
                null,
                false);

        Assert.AreEqual("Song.mp3", Relative(bare, library));
    }

    [TestMethod]
    [DataRow(1.0, @"Saga\Saga #001.cbz")]
    [DataRow(12.0, @"Saga\Saga #012.cbz")]
    [DataRow(0.5, @"Saga\Saga #000.5.cbz")]
    public void Comics_go_under_their_series_with_a_padded_issue_number(double issue, string expected)
    {
        var library = Path.Combine(Folder, "comics");

        var target =
            MediaTypeDefinitions.Comic.ResolveTarget(
                new MediaFileMetadata { Title = "Chapter", Album = "Saga", TrackNumber = issue },
                "cbz",
                library,
                null,
                false);

        Assert.AreEqual(expected, Relative(target, library));
    }

    [TestMethod]
    public void A_comic_with_no_known_series_sits_at_the_top_of_the_library()
    {
        var library = Path.Combine(Folder, "comics");

        var target =
            MediaTypeDefinitions.Comic.ResolveTarget(
                new MediaFileMetadata { Title = "One Shot" },
                "cbr",
                library,
                null,
                false);

        Assert.AreEqual("One Shot.cbr", Relative(target, library));
    }

    [TestMethod]
    public void A_comic_file_name_gives_series_issue_and_year()
    {
        var metadata =
            MediaTypeDefinitions.Comic.ReadMetadata(
                WriteFile("Saga #012 (2013).cbr", "x"));

        Assert.AreEqual("Saga", metadata.Album);
        Assert.AreEqual(12, metadata.TrackNumber);
        Assert.AreEqual(2013, metadata.Year);
    }

    [TestMethod]
    public void A_comic_archive_with_ComicInfo_is_read_from_it()
    {
        var file = Path.Combine(Folder, "whatever.cbz");

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());

            writer.Write(
                "<ComicInfo><Series>Saga</Series><Number>0.5</Number><Title>Annual</Title><Writer>B. Vaughan</Writer><Year>2014</Year></ComicInfo>");
        }

        var metadata = MediaTypeDefinitions.Comic.ReadMetadata(file);

        Assert.IsTrue(metadata.FromEmbeddedTags);
        Assert.AreEqual("Saga", metadata.Album);
        Assert.AreEqual(0.5, metadata.TrackNumber);
        Assert.AreEqual("Annual", metadata.Title);
        Assert.AreEqual(2014, metadata.Year);
        Assert.AreEqual(
            "New comic identified from embedded ComicInfo.xml.",
            MediaTypeDefinitions.Comic.DescribeNewItem(metadata));
    }

    [TestMethod]
    public void The_reason_says_where_the_details_came_from()
    {
        Assert.AreEqual(
            "New track identified from embedded metadata.",
            MediaTypeDefinitions.Music.DescribeNewItem(new MediaFileMetadata { FromEmbeddedTags = true }));

        Assert.AreEqual(
            "New book identified from filename (no embedded metadata found).",
            MediaTypeDefinitions.Book.DescribeNewItem(new MediaFileMetadata()));
    }
}

[TestClass]
public class MediaPromotionServiceTests : TempFolderTest
{
    private DriveProfile _profile = new();
    private MediaCatalogService _catalog = default!;
    private IgnoredItemsService _ignored = default!;
    private MediaPromotionService _service = default!;

    [TestInitialize]
    public void BuildService()
    {
        _profile = new DriveProfile
        {
            Name = "D",
            DownloadPath = Path.Combine(Folder, "downloads"),
            SeedOnlyPath = Path.Combine(Folder, "downloads", "seed-only"),
            BookLibraryPath = Path.Combine(Folder, "books"),
            AudiobookLibraryPath = Path.Combine(Folder, "audiobooks"),
            AudiobookDownloadPath = Path.Combine(Folder, "downloads-audiobooks"),
            MusicLibraryPath = Path.Combine(Folder, "music"),
            MusicDownloadPath = Path.Combine(Folder, "downloads-music"),
            ComicLibraryPath = Path.Combine(Folder, "comics"),
            ComicDownloadPath = Path.Combine(Folder, "downloads-comics")
        };

        foreach (var path in new[]
                 {
                     _profile.DownloadPath, _profile.BookLibraryPath,
                     _profile.AudiobookLibraryPath, _profile.AudiobookDownloadPath,
                     _profile.MusicLibraryPath, _profile.MusicDownloadPath,
                     _profile.ComicLibraryPath, _profile.ComicDownloadPath
                 })
        {
            Directory.CreateDirectory(path);
        }

        var settings = new FixedSettingsService(_profile);
        var parser = new ReleaseParser();

        _catalog = new MediaCatalogService();
        _ignored = new IgnoredItemsService();

        _service = new MediaPromotionService(
            settings,
            new FileInventoryCacheService(new FileScannerService()),
            _catalog,
            new MediaCatalogPopulationService(
                _catalog,
                parser,
                new FfprobeMediaMetadataExtractor(settings)),
            _ignored);
    }

    [TestMethod]
    public async Task A_downloaded_book_that_is_not_in_the_library_is_offered()
    {
        var download = WriteFile(@"downloads\Jane Author - Great Book.epub", "book");

        var found = await _service.ScanAsync(MediaType.Book);

        Assert.HasCount(1, found);

        var candidate = found[0];

        Assert.AreEqual(MediaType.Book, candidate.MediaType);
        Assert.AreEqual("Great Book", candidate.Title);
        Assert.AreEqual("Jane Author", candidate.Author);
        Assert.AreEqual("epub", candidate.Format);
        Assert.AreEqual("D", candidate.DriveProfileName);
        Assert.IsTrue(candidate.CanPromote);
        Assert.AreEqual(download, candidate.Plan!.SourcePath);
        Assert.AreEqual(
            Path.Combine(_profile.BookLibraryPath, "Great Book", "Great Book.epub"),
            candidate.Plan.TargetFilePath);

        // And it is remembered without scanning again.
        Assert.HasCount(1, _service.GetCandidates(MediaType.Book, "D"));
        Assert.IsNotNull(_service.GetLastScanUtc(MediaType.Book, "D"));
    }

    [TestMethod]
    public async Task Once_it_is_in_the_library_it_is_no_longer_offered()
    {
        var download = WriteFile(@"downloads\Jane Author - Great Book.epub", "book");
        var candidate = (await _service.ScanAsync(MediaType.Book)).Single();

        var linked =
            await new HardlinkService().CreateHardlinkAsync(
                download,
                candidate.Plan!.TargetFilePath);

        Assert.IsTrue(linked.Success, linked.ErrorMessage);

        Assert.IsEmpty(await _service.ScanAsync(MediaType.Book));
        Assert.IsEmpty(_service.GetCandidates(MediaType.Book, "D"));
    }

    [TestMethod]
    public async Task Ignored_and_seed_only_downloads_are_not_offered()
    {
        var ignored = WriteFile(@"downloads\Ignored Book.epub", "a");

        WriteFile(@"downloads\seed-only\Seeding Book.epub", "b");
        WriteFile(@"downloads\Wanted Book.epub", "c");

        _ignored.Add(new IgnoredItem { SourcePath = ignored });

        var found = await _service.ScanAsync(MediaType.Book);

        CollectionAssert.AreEqual(
            new[] { "Wanted Book" },
            found.Select(candidate => candidate.Title).ToList());
    }

    [TestMethod]
    public async Task Audio_files_are_only_ever_offered_as_the_type_whose_folder_they_are_in()
    {
        WriteFile(@"downloads-audiobooks\Author - Some Audiobook.mp3", "a");
        WriteFile(@"downloads-music\Artist - Some Song.mp3", "b");

        // A stray audio file in the shared folder belongs to neither.
        WriteFile(@"downloads\stray.mp3", "c");

        var audiobooks = await _service.ScanAsync(MediaType.Audiobook);
        var music = await _service.ScanAsync(MediaType.Music);

        CollectionAssert.AreEqual(
            new[] { "Some Audiobook" },
            audiobooks.Select(candidate => candidate.Title).ToList());

        CollectionAssert.AreEqual(
            new[] { "Some Song" },
            music.Select(candidate => candidate.Title).ToList());

        Assert.AreEqual("Artist", music[0].Author);
        Assert.AreEqual(
            Path.Combine(_profile.MusicLibraryPath, "Artist", "Some Song.mp3"),
            music[0].Plan!.TargetFilePath);
    }

    [TestMethod]
    public async Task What_is_already_in_a_library_is_catalogued_as_the_right_type()
    {
        WriteFile(@"audiobooks\Some Audiobook\Some Audiobook.m4b", "a");
        WriteFile(@"music\Artist\Album\01 - Song.mp3", "b");
        WriteFile(@"comics\Saga\Saga #001.cbr", "c");

        await _service.ScanAsync(MediaType.Audiobook);
        await _service.ScanAsync(MediaType.Music);
        await _service.ScanAsync(MediaType.Comic);

        var byType =
            _catalog.GetAssets()
                .GroupBy(asset => asset.MediaType)
                .ToDictionary(group => group.Key, group => group.Count());

        Assert.AreEqual(1, byType[MediaType.Audiobook]);
        Assert.AreEqual(1, byType[MediaType.Music]);
        Assert.AreEqual(1, byType[MediaType.Comic]);

        var comic = _catalog.GetAssets().Single(asset => asset.MediaType == MediaType.Comic);

        Assert.AreEqual("Saga", comic.Album);
        Assert.AreEqual(1, comic.TrackNumber);
    }

    [TestMethod]
    public async Task A_file_removed_from_the_library_leaves_the_catalog_on_the_next_scan()
    {
        var inLibrary = WriteFile(@"books\Old Book\Old Book.epub", "a");

        await _service.ScanAsync(MediaType.Book);

        Assert.IsNotNull(_catalog.FindAssetByPath(inLibrary));

        File.Delete(inLibrary);

        await _service.ScanAsync(MediaType.Book);

        Assert.IsNull(_catalog.FindAssetByPath(inLibrary));
    }

    [TestMethod]
    public async Task Removing_a_candidate_forgets_it()
    {
        var download = WriteFile(@"downloads\Great Book.epub", "book");

        await _service.ScanAsync(MediaType.Book);

        _service.RemoveCandidate(MediaType.Book, download);

        Assert.IsEmpty(_service.GetCandidates(MediaType.Book, "D"));
    }

    [TestMethod]
    public async Task Nothing_is_offered_for_a_type_with_no_library_folder_set()
    {
        _profile.ComicLibraryPath = string.Empty;

        WriteFile(@"downloads-comics\Saga #001.cbz", "not a real zip");

        Assert.IsEmpty(await _service.ScanAsync(MediaType.Comic));
    }
}
