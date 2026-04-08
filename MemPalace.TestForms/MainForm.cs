using System.Drawing;
using System.Windows.Forms;
using MemPalace.Core;
using MemPalace.Services;

namespace MemPalace.TestForms;

/// <summary>
/// Interactive test harness for MemPalace.
/// Tabs: Mine | Search | Embeddings | Embed | Status | Knowledge Graph
/// </summary>
public class MainForm : Form
{
    // ── Services ──────────────────────────────────────────────────────────────
    private AppConfig?             _config;
    private DatabaseService?       _db;
    private MinerService?          _miner;
    private SearchService?         _searcher;
    private EmbeddingService?      _embedder;
    private KnowledgeGraphService? _kg;

    // ── Status strip ──────────────────────────────────────────────────────────
    private readonly ToolStripLabel _lblModel = new() { Text = "Model: initialising…" };
    private readonly ToolStripLabel _lblDb    = new() { Text = "DB: –" };

    // ── Mine tab controls ─────────────────────────────────────────────────────
    private readonly TextBox      _minePath   = new() { Width = 420 };
    private readonly TextBox      _mineDomain = new() { Width = 180 };
    private readonly ComboBox     _mineMode   = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox     _mineDryRun = new() { Text = "Dry run", AutoSize = true };
    private readonly Button       _mineBtn    = Btn("Mine");
    private readonly RichTextBox  _mineLog    = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true,
        BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.FromArgb(180, 255, 160),
        Font = new Font("Consolas", 9f), ScrollBars = RichTextBoxScrollBars.Vertical,
    };

    // ── Search tab controls ───────────────────────────────────────────────────
    private readonly TextBox       _srchQuery  = new() { Width = 420 };
    private readonly TextBox       _srchDomain = new() { Width = 160 };
    private readonly TextBox       _srchTopic  = new() { Width = 160 };
    private readonly NumericUpDown _srchLimit  = Num(10, 1, 200);
    private readonly Button        _srchBtn    = Btn("Search");
    private readonly DataGridView  _srchGrid   = Grid();

    // ── Embeddings tab controls ───────────────────────────────────────────────
    private readonly Label         _embCovLbl   = new() { AutoSize = true, Text = "Coverage: –" };
    private readonly TextBox       _embDomain   = new() { Width = 160 };
    private readonly TextBox       _embTopic    = new() { Width = 160 };
    private readonly NumericUpDown _embLimit    = Num(20,  1, 2000);
    private readonly NumericUpDown _embDims     = Num(8,   1, 384);
    private readonly Button        _embListBtn  = Btn("List");
    private readonly TextBox       _embQuery    = new() { Width = 420 };
    private readonly NumericUpDown _embQLimit   = Num(10,  1, 200);
    private readonly Button        _embQBtn     = Btn("Query similarity");
    private readonly DataGridView  _embGrid     = Grid();

    // ── Embed (retroactive) tab controls ─────────────────────────────────────
    private readonly Label         _embedPendLbl = new() { AutoSize = true, Text = "Pending: –" };
    private readonly TextBox       _embedDomain  = new() { Width = 160 };
    private readonly TextBox       _embedTopic   = new() { Width = 160 };
    private readonly NumericUpDown _embedBatch   = Num(32, 1, 512);
    private readonly Button        _embedRunBtn  = Btn("Run Embed");
    private readonly Button        _embedRefBtn  = Btn("↻ Refresh");
    private readonly ProgressBar   _embedPbar    = new() { Dock = DockStyle.Top, Height = 22, Style = ProgressBarStyle.Continuous };
    private readonly Label         _embedProgLbl = new() { AutoSize = true, Text = "" };

    // ── Status tab controls ───────────────────────────────────────────────────
    private readonly Button      _statRefBtn = Btn("↻ Refresh");
    private readonly RichTextBox _statBox    = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true,
        Font = new Font("Consolas", 10f), ScrollBars = RichTextBoxScrollBars.Vertical,
    };

    // ── KG tab controls ───────────────────────────────────────────────────────
    private readonly TextBox   _kgEName   = new() { Width = 200 };
    private readonly TextBox   _kgEType   = new() { Width = 120, Text = "concept" };
    private readonly Button    _kgAddEBtn = Btn("Add Entity");
    private readonly TextBox   _kgSub     = new() { Width = 150 };
    private readonly TextBox   _kgPred    = new() { Width = 150 };
    private readonly TextBox   _kgObj     = new() { Width = 150 };
    private readonly Button    _kgAddTBtn = Btn("Add Triple");
    private readonly TextBox   _kgQEnt    = new() { Width = 220 };
    private readonly ComboBox  _kgDir     = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button    _kgQBtn    = Btn("Query");
    private readonly DataGridView _kgGrid = Grid();
    private readonly Label     _kgStatus  = new() { AutoSize = true };

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainForm()
    {
        Text          = "MemPalace Test Harness";
        Size          = new Size(1150, 780);
        MinimumSize   = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        Shown += async (_, _) => await InitServicesAsync();
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f) };
        tabs.TabPages.Add(BuildMineTab());
        tabs.TabPages.Add(BuildSearchTab());
        tabs.TabPages.Add(BuildEmbeddingsTab());
        tabs.TabPages.Add(BuildEmbedTab());
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildKgTab());

        var strip = new StatusStrip();
        strip.Items.Add(_lblModel);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(_lblDb);

        Controls.Add(tabs);
        Controls.Add(strip);
    }

    // ─────────────────────────────── Mine ────────────────────────────────────

    private TabPage BuildMineTab()
    {
        var page = new TabPage("Mine");

        _mineMode.Items.AddRange(new object[] { "projects", "convos", "general" });
        _mineMode.SelectedIndex = 0;

        var browseBtn = Btn("Browse…");
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Select directory to mine" };
            if (dlg.ShowDialog() == DialogResult.OK)
                _minePath.Text = dlg.SelectedPath;
        };

        _mineBtn.Click += async (_, _) => await DoMineAsync();

        var top = VStack(
            Row("Source dir:", _minePath, browseBtn),
            Row("Domain:", _mineDomain),
            Row("Mode:", _mineMode, _mineDryRun, _mineBtn)
        );

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 115, Panel1MinSize = 100, Panel2MinSize = 80,
        };
        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(_mineLog);

        page.Controls.Add(split);
        return page;
    }

    // ─────────────────────────────── Search ──────────────────────────────────

    private TabPage BuildSearchTab()
    {
        var page = new TabPage("Search");

        _srchBtn.Click += async (_, _) => await DoSearchAsync();
        _srchQuery.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) await DoSearchAsync(); };

        AddColumns(_srchGrid,
            ("Score",   55,  5),
            ("Domain",  130, 11),
            ("Topic",   110, 9),
            ("Title",   190, 16),
            ("Snippet", 0,   59));

        var top = VStack(
            Row("Query:", _srchQuery, _srchBtn),
            Row("Domain:", _srchDomain, Lbl("Topic:"), _srchTopic, Lbl("Limit:"), _srchLimit)
        );

        page.Controls.Add(FillPanel(_srchGrid, top));
        return page;
    }

    // ─────────────────────────────── Embeddings ──────────────────────────────

    private TabPage BuildEmbeddingsTab()
    {
        var page = new TabPage("Embeddings");

        var refreshCov = Btn("↻");
        refreshCov.Width = 30;
        refreshCov.Click += (_, _) => RefreshEmbCoverage();

        _embListBtn.Click += async (_, _) => await DoEmbListAsync();
        _embQBtn.Click    += async (_, _) => await DoEmbQueryAsync();
        _embQuery.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) await DoEmbQueryAsync(); };

        AddColumns(_embGrid,
            ("Chunk ID",    90,  7),
            ("Domain",     130, 10),
            ("Topic",      110,  8),
            ("Title",      170, 13),
            ("Norm",        55,  4),
            ("Similarity",  75,  6),
            ("Dimensions",   0, 52));

        var top = VStack(
            Row(_embCovLbl, refreshCov),
            Separator("── List ─────────────────────────────────────────────"),
            Row("Domain:", _embDomain, Lbl("Topic:"), _embTopic, Lbl("Limit:"), _embLimit, Lbl("Dims:"), _embDims, _embListBtn),
            Separator("── Query by cosine similarity ────────────────────────"),
            Row("Query:", _embQuery, Lbl("Limit:"), _embQLimit, _embQBtn)
        );

        page.Controls.Add(FillPanel(_embGrid, top));
        return page;
    }

    // ─────────────────────────────── Embed ───────────────────────────────────

    private TabPage BuildEmbedTab()
    {
        var page = new TabPage("Embed");

        _embedRunBtn.Click += async (_, _) => await DoEmbedAsync();
        _embedRefBtn.Click += (_, _) => RefreshEmbedPending();

        var top = VStack(
            Row(_embedPendLbl, _embedRefBtn),
            Row("Domain:", _embedDomain, Lbl("Topic:"), _embedTopic, Lbl("Batch size:"), _embedBatch),
            Row(_embedRunBtn),
            Row(_embedProgLbl)
        );

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_embedPbar);
        panel.Controls.Add(top);

        page.Controls.Add(panel);
        return page;
    }

    // ─────────────────────────────── Status ──────────────────────────────────

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Status");

        _statRefBtn.Click += async (_, _) => await RefreshStatusAsync();

        var top = VStack(Row(_statRefBtn));

        page.Controls.Add(FillPanel(_statBox, top));
        return page;
    }

    // ─────────────────────────────── KG ──────────────────────────────────────

    private TabPage BuildKgTab()
    {
        var page = new TabPage("Knowledge Graph");

        _kgDir.Items.AddRange(new object[] { "out", "in", "both" });
        _kgDir.SelectedIndex = 0;

        _kgAddEBtn.Click += async (_, _) => await DoKgAddEntityAsync();
        _kgAddTBtn.Click += async (_, _) => await DoKgAddTripleAsync();
        _kgQBtn.Click    += async (_, _) => await DoKgQueryAsync();
        _kgQEnt.KeyDown  += async (_, e) => { if (e.KeyCode == Keys.Enter) await DoKgQueryAsync(); };

        AddColumns(_kgGrid,
            ("Subject",    0, 22),
            ("Predicate",  0, 22),
            ("Object",     0, 22),
            ("Current",   60,  5),
            ("Confidence", 70,  5),
            ("Valid from", 100, 12),
            ("Valid to",   100, 12));

        var top = VStack(
            Separator("── Add Entity ──────────────────────────────────────"),
            Row("Name:", _kgEName, Lbl("Type:"), _kgEType, _kgAddEBtn),
            Separator("── Add Triple ──────────────────────────────────────"),
            Row("Subject:", _kgSub, Lbl("Predicate:"), _kgPred, Lbl("Object:"), _kgObj, _kgAddTBtn),
            Separator("── Query Entity ────────────────────────────────────"),
            Row("Entity:", _kgQEnt, Lbl("Direction:"), _kgDir, _kgQBtn, _kgStatus)
        );

        page.Controls.Add(FillPanel(_kgGrid, top));
        return page;
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 2),
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(2, 3, 4, 0);
            row.Controls.Add(c);
        }
        return row;
    }

    private static FlowLayoutPanel Row(string label, params Control[] controls)
    {
        var all = new Control[controls.Length + 1];
        all[0] = Lbl(label);
        controls.CopyTo(all, 1);
        return Row(all);
    }

    private static FlowLayoutPanel VStack(params Control[] rows)
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8),
        };
        foreach (var r in rows) stack.Controls.Add(r);
        return stack;
    }

    private static Panel FillPanel(Control fillControl, Control topControl)
    {
        var p = new Panel { Dock = DockStyle.Fill };
        p.Controls.Add(fillControl);
        p.Controls.Add(topControl);
        return p;
    }

    private static Label Lbl(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(6, 6, 2, 0) };

    private static Label Separator(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(2, 4, 0, 0) };

    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Height = 26 };

    private static NumericUpDown Num(int val, int min, int max) =>
        new() { Value = val, Minimum = min, Maximum = max, Width = 70 };

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
    };

    private static void AddColumns(DataGridView grid,
        params (string Name, int Width, int FillWeight)[] cols)
    {
        foreach (var (name, width, fill) in cols)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = name,
                FillWeight = fill,
                MinimumWidth = Math.Max(width, 40),
            };
            if (width > 0) col.Width = width;
            grid.Columns.Add(col);
        }
    }

    // ── Service init ──────────────────────────────────────────────────────────

    private async Task InitServicesAsync()
    {
        try
        {
            _config = ConfigService.LoadOrCreate();
            _db     = new DatabaseService(_config);
            _kg     = new KnowledgeGraphService(_config);

            _lblDb.Text = $"DB: {_config.DbPath}";

            // Load embedding model best-effort
            try
            {
                if (!ModelDownloader.IsAvailable(_config.EmbeddingModelDir))
                {
                    _lblModel.Text = "Model: downloading…";
                    await ModelDownloader.EnsureModelAsync(_config.EmbeddingModelDir);
                }
                _embedder = await EmbeddingService.TryCreateAsync(_config.EmbeddingModelDir);
            }
            catch (Exception ex)
            {
                _lblModel.Text = $"Model: unavailable — {Trunc(ex.Message, 60)}";
            }

            _miner    = new MinerService(_config, _db, _embedder);
            _searcher = new SearchService(_config, _db, _embedder);

            UpdateModelLabel();
            RefreshEmbCoverage();
            RefreshEmbedPending();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Init failed:\n{ex.Message}",
                "MemPalace", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Mine ─────────────────────────────────────────────────────────────────

    private async Task DoMineAsync()
    {
        if (_miner is null) return;
        var path = _minePath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) { MineLog("Enter a source directory first."); return; }

        _mineBtn.Enabled = false;
        _mineLog.Clear();
        MineLog($"Mining: {path}");

        try
        {
            var mode   = _mineMode.SelectedItem?.ToString() ?? "projects";
            var domain = NullIfBlank(_mineDomain.Text);
            var dry    = _mineDryRun.Checked;

            int filesScanned, chunksAdded, filesSkipped;
            Dictionary<string, int> topicCounts;

            if (mode == "convos")
            {
                var cm  = new ConvoMinerService(_config!, _db!);
                var res = await cm.MineConversationsAsync(path, domain, 0, dry);
                (filesScanned, chunksAdded, filesSkipped, topicCounts) =
                    (res.FilesScanned, res.ChunksAdded, res.FilesSkipped, res.TopicCounts);
            }
            else
            {
                var res = await _miner.MineAsync(path, mode, domain, 0, dry);
                (filesScanned, chunksAdded, filesSkipped, topicCounts) =
                    (res.FilesScanned, res.ChunksAdded, res.FilesSkipped, res.TopicCounts);
            }

            MineLog($"Done: {filesScanned} scanned  {chunksAdded} added  {filesSkipped} skipped");
            foreach (var (topic, count) in topicCounts.OrderByDescending(x => x.Value))
                MineLog($"  {topic}: {count}");

            RefreshEmbCoverage();
            RefreshEmbedPending();
            UpdateModelLabel();
        }
        catch (Exception ex) { MineLog("ERROR: " + ex.Message); }
        finally { _mineBtn.Enabled = true; }
    }

    private void MineLog(string msg)
    {
        if (InvokeRequired) { Invoke(() => MineLog(msg)); return; }
        _mineLog.AppendText(msg + "\n");
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private async Task DoSearchAsync()
    {
        if (_searcher is null || string.IsNullOrWhiteSpace(_srchQuery.Text)) return;
        _srchBtn.Enabled = false;
        _srchGrid.Rows.Clear();
        try
        {
            var results = await _searcher.SearchAsync(
                _srchQuery.Text.Trim(),
                NullIfBlank(_srchDomain.Text),
                NullIfBlank(_srchTopic.Text),
                (int)_srchLimit.Value);

            foreach (var r in results)
                _srchGrid.Rows.Add(r.Score.ToString("F3"), r.Domain, r.Topic, r.Title,
                    Flatten(r.Snippet));
        }
        catch (Exception ex)
        { MessageBox.Show(ex.Message, "Search", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _srchBtn.Enabled = true; }
    }

    // ── Embeddings list ───────────────────────────────────────────────────────

    private async Task DoEmbListAsync()
    {
        if (_db is null) return;
        _embListBtn.Enabled = false;
        _embGrid.Rows.Clear();
        try
        {
            var rows  = await Task.Run(() => _db.GetEmbeddingsWithMeta(
                NullIfBlank(_embDomain.Text), NullIfBlank(_embTopic.Text)));
            int limit = (int)_embLimit.Value;
            int dims  = (int)_embDims.Value;

            foreach (var row in rows.Take(limit))
            {
                float norm = Norm(row.Embedding);
                _embGrid.Rows.Add(
                    row.ChunkId[..Math.Min(8, row.ChunkId.Length)],
                    row.Domain, row.Topic, row.Title,
                    norm.ToString("F3"), "–",
                    DimStr(row.Embedding, dims));
            }
            if (rows.Count > limit)
                _embGrid.Rows.Add("…", $"({rows.Count - limit} more)", "", "", "", "", "");
        }
        catch (Exception ex)
        { MessageBox.Show(ex.Message, "Embeddings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _embListBtn.Enabled = true; }
    }

    // ── Embeddings query ──────────────────────────────────────────────────────

    private async Task DoEmbQueryAsync()
    {
        if (_db is null || string.IsNullOrWhiteSpace(_embQuery.Text)) return;
        if (_embedder is null)
        {
            MessageBox.Show("Embedding model not loaded.", "Query", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _embQBtn.Enabled = false;
        _embGrid.Rows.Clear();
        try
        {
            var queryVec = await Task.Run(() => _embedder.Embed(_embQuery.Text.Trim()));
            var allRows  = await Task.Run(() => _db.GetEmbeddingsWithMeta(
                NullIfBlank(_embDomain.Text), NullIfBlank(_embTopic.Text)));
            int limit = (int)_embQLimit.Value;
            int dims  = (int)_embDims.Value;

            var ranked = allRows
                .Select(r => (r, Sim: EmbeddingService.CosineSimilarity(queryVec, r.Embedding)))
                .OrderByDescending(x => x.Sim)
                .Take(limit);

            foreach (var (row, sim) in ranked)
                _embGrid.Rows.Add(
                    row.ChunkId[..Math.Min(8, row.ChunkId.Length)],
                    row.Domain, row.Topic, row.Title,
                    Norm(row.Embedding).ToString("F3"),
                    sim.ToString("F3"),
                    DimStr(row.Embedding, dims));
        }
        catch (Exception ex)
        { MessageBox.Show(ex.Message, "Query", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _embQBtn.Enabled = true; }
    }

    // ── Retroactive embed ─────────────────────────────────────────────────────

    private async Task DoEmbedAsync()
    {
        if (_db is null || _embedder is null)
        {
            MessageBox.Show("Embedding model not available.", "Embed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _embedRunBtn.Enabled = false;
        _embedProgLbl.Text   = "";

        try
        {
            var pending = _db.GetChunksWithoutEmbeddings(
                NullIfBlank(_embedDomain.Text), NullIfBlank(_embedTopic.Text));

            if (pending.Count == 0)
            {
                _embedProgLbl.Text = "All chunks already have embeddings.";
                return;
            }

            int batchSz = (int)_embedBatch.Value;
            _embedPbar.Minimum = 0;
            _embedPbar.Maximum = pending.Count;
            _embedPbar.Value   = 0;
            int done = 0, errors = 0;

            for (int i = 0; i < pending.Count; i += batchSz)
            {
                var batch = pending.Skip(i).Take(batchSz).ToList();
                try
                {
                    var vecs = await Task.Run(() => _embedder.EmbedBatch(
                        batch.Select(c => c.Content).ToList()));
                    for (int j = 0; j < batch.Count; j++)
                        _db.UpsertEmbedding(batch[j].Id, vecs[j]);
                    done += batch.Count;
                }
                catch { errors += batch.Count; }

                _embedPbar.Value   = Math.Min(done + errors, pending.Count);
                _embedProgLbl.Text = $"{done + errors}/{pending.Count}  ({done} ok, {errors} errors)";
                Application.DoEvents();
            }

            _embedProgLbl.Text = $"Done — {done} embeddings stored, {errors} errors.";
            RefreshEmbCoverage();
            RefreshEmbedPending();
            UpdateModelLabel();
        }
        catch (Exception ex)
        { MessageBox.Show(ex.Message, "Embed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _embedRunBtn.Enabled = true; }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private async Task RefreshStatusAsync()
    {
        if (_db is null || _kg is null || _config is null) return;
        try
        {
            var layers = new LayerService(_config, _db, _kg);
            var status = await layers.StatusAsync();
            var kg     = (KgStats)status["kg_stats"];
            int embs   = _db.GetEmbeddingCount();
            int chunks = _db.GetChunkCount();

            _statBox.Text =
                $"Store DB:       {_config.DbPath}\n" +
                $"KG DB:          {_config.KgDbPath}\n" +
                $"Model dir:      {_config.EmbeddingModelDir}\n" +
                $"\n" +
                $"Chunks:         {status["chunk_count"]:N0}\n" +
                $"Domains:        {status["domain_count"]:N0}\n" +
                $"Topics:         {status["topic_count"]:N0}\n" +
                $"Embeddings:     {embs:N0} / {chunks:N0}  " +
                    $"({(chunks == 0 ? 0 : 100.0 * embs / chunks):F0}% coverage)\n" +
                $"\n" +
                $"KG Entities:    {kg.EntityCount:N0}\n" +
                $"KG Triples:     {kg.TripleCount:N0}  (active: {kg.ActiveTripleCount:N0})\n" +
                $"\n" +
                $"Identity:       {(bool)status["has_identity"]}";
        }
        catch (Exception ex) { _statBox.Text = "Error: " + ex.Message; }
    }

    // ── Knowledge Graph ───────────────────────────────────────────────────────

    private async Task DoKgAddEntityAsync()
    {
        if (_kg is null || string.IsNullOrWhiteSpace(_kgEName.Text)) return;
        try
        {
            var name = _kgEName.Text.Trim();
            var id   = await Task.Run(() => _kg.AddEntity(name, _kgEType.Text.Trim()));
            _kgStatus.Text = $"✓ Entity added: {id}";
            _kgEName.Clear();
        }
        catch (Exception ex) { _kgStatus.Text = "✗ " + ex.Message; }
    }

    private async Task DoKgAddTripleAsync()
    {
        if (_kg is null) return;
        if (string.IsNullOrWhiteSpace(_kgSub.Text) ||
            string.IsNullOrWhiteSpace(_kgPred.Text) ||
            string.IsNullOrWhiteSpace(_kgObj.Text)) return;
        try
        {
            var id = await Task.Run(() => _kg.AddTriple(
                _kgSub.Text.Trim(), _kgPred.Text.Trim(), _kgObj.Text.Trim()));
            _kgStatus.Text = $"✓ Triple added: {id[..Math.Min(8, id.Length)]}…";
            _kgSub.Clear(); _kgPred.Clear(); _kgObj.Clear();
        }
        catch (Exception ex) { _kgStatus.Text = "✗ " + ex.Message; }
    }

    private async Task DoKgQueryAsync()
    {
        if (_kg is null || string.IsNullOrWhiteSpace(_kgQEnt.Text)) return;
        _kgGrid.Rows.Clear();
        try
        {
            var dir   = _kgDir.SelectedItem?.ToString() ?? "out";
            var facts = await Task.Run(() => _kg.QueryEntity(_kgQEnt.Text.Trim(), direction: dir));
            foreach (var f in facts)
                _kgGrid.Rows.Add(
                    f.Subject, f.Predicate, f.Object,
                    f.IsCurrent ? "✓" : "✗",
                    f.Confidence.ToString("F2"),
                    f.ValidFrom ?? "–",
                    f.ValidTo   ?? "–");
            _kgStatus.Text = $"{facts.Count} fact(s)";
        }
        catch (Exception ex) { _kgStatus.Text = "✗ " + ex.Message; }
    }

    // ── Refresh helpers ───────────────────────────────────────────────────────

    private void RefreshEmbCoverage()
    {
        if (_db is null) return;
        int embs  = _db.GetEmbeddingCount();
        int total = _db.GetChunkCount();
        _embCovLbl.Text = $"Coverage: {embs:N0} / {total:N0} chunks  " +
                          $"({(total == 0 ? 0 : 100.0 * embs / total):F0}%)";
    }

    private void RefreshEmbedPending()
    {
        if (_db is null) return;
        int n = _db.GetChunksWithoutEmbeddings().Count;
        _embedPendLbl.Text = $"Pending: {n:N0} chunk(s) without embeddings";
    }

    private void UpdateModelLabel()
    {
        if (_db is null) return;
        _lblModel.Text = _embedder is not null
            ? $"Model: loaded  ({_db.GetEmbeddingCount():N0} embeddings)"
            : "Model: not loaded  (BM25-only)";
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    private static string? NullIfBlank(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Flatten(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ');

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static float Norm(float[] v) =>
        MathF.Sqrt(v.Sum(x => x * x));

    private static string DimStr(float[] v, int n) =>
        string.Join("  ", v.Take(n).Select(x => x.ToString("F3")));
}
