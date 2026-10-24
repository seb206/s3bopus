using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Xunit;

namespace S3bopus
{
    public sealed class ArchivesTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "s3bopus-tests-" + Guid.NewGuid().ToString("N"));

        public ArchivesTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        /// <summary>ZIP sans entrées de dossiers (comme beaucoup d'archives réelles).</summary>
        string MakeZip(params (string name, string content)[] files)
        {
            string zip = Path.Combine(_dir, "test.zip");
            using var a = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var (name, content) in files)
                using (var w = new StreamWriter(a.CreateEntry(name).Open())) w.Write(content);
            return zip;
        }

        [Fact]
        public void Split_finds_archive_and_inner_path()
        {
            string zip = MakeZip(("docs/a.txt", "A"));
            string sep = Path.DirectorySeparatorChar.ToString();
            Assert.True(Archives.Split(zip + sep + "docs" + sep + "sub", out var file, out var inner));
            Assert.Equal(zip, file);
            Assert.Equal("docs/sub", inner);
            Assert.True(Archives.Split(zip, out _, out inner));
            Assert.Equal("", inner);
            Assert.False(Archives.Split(_dir, out _, out _));
        }

        [Fact]
        public void List_creates_implicit_folders_and_children_are_one_level()
        {
            string zip = MakeZip(("readme.txt", "hello"), ("docs/a.txt", "A"), ("docs/deep/b.txt", "BB"));
            var all = Archives.List(zip);
            Assert.Contains(all, i => i.Key == "docs" && i.IsDir);
            Assert.Contains(all, i => i.Key == "docs/deep" && i.IsDir);

            var root = Archives.Children(all, "").Select(i => i.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "docs", "readme.txt" }, root);
            var docs = Archives.Children(all, "docs").Select(i => i.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "a.txt", "deep" }, docs);
            Assert.Equal(5, all.Single(i => i.Key == "readme.txt").Size);
        }

        [Fact]
        public void Extract_folder_keeps_its_structure()
        {
            string zip = MakeZip(("docs/a.txt", "A"), ("docs/deep/b.txt", "BB"), ("other.txt", "X"));
            string dest = Path.Combine(_dir, "out");
            Directory.CreateDirectory(dest);
            var top = Archives.Extract(zip, new[] { "docs" }, dest);
            Assert.Equal(new[] { Path.Combine(dest, "docs") }, top);
            Assert.Equal("BB", File.ReadAllText(Path.Combine(dest, "docs", "deep", "b.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "other.txt")));
        }

        [Fact]
        public void Extract_refuses_paths_leaving_the_destination()
        {
            string zip = MakeZip(("../evil.txt", "boom"));
            string dest = Path.Combine(_dir, "out");
            Directory.CreateDirectory(dest);
            // Norm() enlève « ../ » en tête : le fichier reste dans la destination au lieu d'en sortir
            Archives.Extract(zip, new[] { "evil.txt" }, dest);
            Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "evil.txt")));
        }

        [Fact]
        public void Extract_refuses_dot_dot_inside_the_path()
        {
            string zip = MakeZip(("docs/../../evil.txt", "boom"));
            string dest = Path.Combine(_dir, "out");
            Directory.CreateDirectory(dest);
            Assert.Throws<IOException>(() => Archives.Extract(zip, new[] { "docs" }, dest));
            Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        }

        [Theory]
        [InlineData("./a/b/", "a/b")]
        [InlineData("/../x", "x")]
        [InlineData(".bashrc", ".bashrc")]
        [InlineData("dir\\.hidden\\f", "dir/.hidden/f")]
        [InlineData("..", "")]
        public void Norm_cleans_keys_but_keeps_hidden_files(string raw, string expected) => Assert.Equal(expected, Archives.Norm(raw));

        [Fact]
        public void Tar_gz_is_listed_and_extracted()
        {
            string src = Path.Combine(_dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "dir"));
            File.WriteAllText(Path.Combine(src, "dir", "f.txt"), "tar!");
            string tgz = Path.Combine(_dir, "t.tar.gz");
            using (var fs = File.Create(tgz))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                TarFile.CreateFromDirectory(src, gz, false);

            var all = Archives.List(tgz);
            Assert.Contains(all, i => i.Key == "dir/f.txt" && !i.IsDir);
            string dest = Path.Combine(_dir, "out");
            Directory.CreateDirectory(dest);
            Archives.Extract(tgz, new[] { "dir/f.txt" }, dest);
            Assert.Equal("tar!", File.ReadAllText(Path.Combine(dest, "f.txt")));
        }

        [Fact]
        public void IsArchive_recognises_supported_extensions()
        {
            foreach (var n in new[] { "a.zip", "a.7z", "a.RAR", "a.tar", "a.tar.gz", "a.tgz", "a.tar.xz" }) Assert.True(Archives.IsArchive(n), n);
            Assert.False(Archives.IsArchive("a.txt"));
        }
    }

    public sealed class PathHistoryTests
    {
        [Fact]
        public void Most_recent_first_without_duplicates_and_capped()
        {
            PathHistory.Clear();
            PathHistory.Add("/a");
            PathHistory.Add("/b");
            PathHistory.Add("/a");
            Assert.Equal(new[] { "/a", "/b" }, PathHistory.Items);
            for (int i = 0; i < 40; i++) PathHistory.Add("/p" + i);
            Assert.Equal(25, PathHistory.Items.Count);
            Assert.Equal("/p39", PathHistory.Items[0]);
            PathHistory.Clear();
        }
    }

    public sealed class TransferQueueTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "s3bopus-tests-" + Guid.NewGuid().ToString("N"));
        public TransferQueueTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        static void WaitDone(TransferJob job)
        {
            for (int i = 0; i < 200 && !job.IsFinished; i++) Thread.Sleep(25);
            Assert.True(job.IsFinished, "la tâche n'a pas fini");
        }

        [Fact]
        public void Copies_a_folder_and_skips_on_conflict()
        {
            string src = Path.Combine(_dir, "src"), dst = Path.Combine(_dir, "dst");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "x.txt"), "new");
            Directory.CreateDirectory(Path.Combine(dst, "src", "sub"));
            File.WriteAllText(Path.Combine(dst, "src", "sub", "x.txt"), "old");

            var q = new TransferQueue { ResolveConflict = (j, info) => ConflictChoice.Skip };
            var job = q.Enqueue(JobKind.Copy, new[] { src }, dst);
            WaitDone(job);
            Assert.Equal(JobState.Done, job.State);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "src", "sub", "x.txt")));
            Assert.True(File.Exists(Path.Combine(src, "sub", "x.txt")));
        }

        [Fact]
        public void Move_on_same_disk_renames()
        {
            string f = Path.Combine(_dir, "f.txt"), dst = Path.Combine(_dir, "d");
            File.WriteAllText(f, "data");
            Directory.CreateDirectory(dst);
            var job = new TransferQueue().Enqueue(JobKind.Move, new[] { f }, dst);
            WaitDone(job);
            Assert.Equal(JobState.Done, job.State);
            Assert.False(File.Exists(f));
            Assert.Equal("data", File.ReadAllText(Path.Combine(dst, "f.txt")));
        }
    }
}
