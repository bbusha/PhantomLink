using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PhantomLink.Core
{
    public sealed class DiscoveryClient
    {
        public async Task<long> StartScanAsync(int maxCandidates = 12000, string kindsCsv = "numeric,bool,count,enum", int timeoutMs = 20000)
        {
            return await StartScanAsync(maxCandidates, kindsCsv, "mono", 2, timeoutMs);
        }

        public async Task<long> StartScanAsync(int maxCandidates, string kindsCsv, string scope, int budgetMs, int timeoutMs = 20000)
        {
            scope ??= "mono";
            budgetMs = Math.Max(1, Math.Min(budgetMs, 16));
            var cmd = $"DISCOVERY_SCAN|max={maxCandidates}|kinds={kindsCsv}|scope={scope}|budgetMs={budgetMs}";
            var resp = await IPCMeloaderClient.SendCommandAsync(cmd, timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_SCAN|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var kv = ParsePipeKeyValues(resp);
            if (!kv.TryGetValue("scanId", out var scanIdText) || !long.TryParse(scanIdText, out var scanId))
                throw new InvalidOperationException("Missing scanId");
            return scanId;
        }

        public async Task<DiscoveryScanStatus> GetScanStatusAsync(long scanId, int timeoutMs = 12000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_SCAN_STATUS|{scanId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_SCAN_STATUS|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var kv = ParsePipeKeyValues(resp);
            var status = new DiscoveryScanStatus { ScanId = scanId };
            if (kv.TryGetValue("running", out var running))
                status.Running = running == "1";
            if (kv.TryGetValue("done", out var done))
                status.Done = done == "1";
            if (kv.TryGetValue("progress", out var p) && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var prog))
                status.Progress = prog;
            if (kv.TryGetValue("processed", out var processed) && int.TryParse(processed, out var pr))
                status.Processed = pr;
            if (kv.TryGetValue("total", out var total) && int.TryParse(total, out var t))
                status.Total = t;
            if (kv.TryGetValue("candidates", out var candidates) && int.TryParse(candidates, out var c))
                status.Candidates = c;
            if (kv.TryGetValue("stage", out var stage))
                status.Stage = UnescapePipe(stage);
            return status;
        }

        public async Task<bool> CancelScanAsync(long scanId, int timeoutMs = 12000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_SCAN_CANCEL|{scanId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_SCAN_CANCEL|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            var kv = ParsePipeKeyValues(resp);
            return kv.TryGetValue("canceled", out var c) && c == "1";
        }

        public async Task<DiscoveryScan> FetchScanAsync(long scanId, int pageSize = 900, int timeoutMs = 20000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));

            var scan = new DiscoveryScan
            {
                ScanId = scanId,
                CreatedUtc = DateTime.UtcNow
            };

            var offset = 0;
            var total = int.MaxValue;
            while (offset < total)
            {
                var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_SCAN_PAGE|{scanId}|{offset}|{pageSize}", timeoutMs);
                if (IPCMeloaderClient.TryParseError(resp, out var err))
                    throw new InvalidOperationException(err);
                if (resp == null || !resp.StartsWith("DISCOVERY_SCAN_PAGE|", StringComparison.Ordinal))
                    throw new InvalidOperationException(resp ?? "No response");

                ParseScanPage(resp, scan, out var pageOffset, out var pageTotal, out var pageCount);
                total = pageTotal;
                offset = pageOffset + pageCount;
                if (pageCount == 0)
                    break;
            }

            return scan;
        }

        public async Task<DiscoverySnapshot> SnapshotAsync(long scanId, IEnumerable<string> keys, int timeoutMs = 15000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
            var snapshot = new DiscoverySnapshot { ScanId = scanId };

            var chunkSize = 900;
            for (var i = 0; i < keyList.Count; i += chunkSize)
            {
                var chunk = keyList.Skip(i).Take(chunkSize).ToList();
                var cmd = new StringBuilder();
                cmd.Append("DISCOVERY_SNAPSHOT|");
                cmd.Append(scanId);
                for (var j = 0; j < chunk.Count; j++)
                {
                    cmd.Append('|');
                    cmd.Append(chunk[j]);
                }

                var resp = await IPCMeloaderClient.SendCommandAsync(cmd.ToString(), timeoutMs);
                if (IPCMeloaderClient.TryParseError(resp, out var err))
                    throw new InvalidOperationException(err);
                if (resp == null || !resp.StartsWith("DISCOVERY_SNAPSHOT|", StringComparison.Ordinal))
                    throw new InvalidOperationException(resp ?? "No response");

                var part = ParseSnapshot(resp);
                snapshot.Ticks = part.Ticks;
                foreach (var kv in part.Values)
                    snapshot.Values[kv.Key] = kv.Value;
            }

            return snapshot;
        }

        public async Task<DiscoveryWriteProbeResult> WriteProbeAsync(long scanId, string key, double? magnitude = null, int delayMs = 250, bool revert = true, int timeoutMs = 5000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            delayMs = Math.Max(0, Math.Min(1200, delayMs));

            var cmd = new StringBuilder();
            cmd.Append("DISCOVERY_WRITE_PROBE|");
            cmd.Append(scanId);
            cmd.Append('|');
            cmd.Append(key);
            cmd.Append("|delayMs=");
            cmd.Append(delayMs.ToString(CultureInfo.InvariantCulture));
            cmd.Append("|revert=");
            cmd.Append(revert ? "1" : "0");
            if (magnitude.HasValue)
            {
                cmd.Append("|mag=");
                cmd.Append(magnitude.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            var resp = await IPCMeloaderClient.SendCommandAsync(cmd.ToString(), timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_WRITE_PROBE|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            return ParseWriteProbe(resp);
        }

        public async Task<DiscoveryExperimentBeginResult> BeginExperimentAsync(long scanId, string label, IEnumerable<string> keys, int timeoutMs = 15000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            label ??= "experiment";
            var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(1100).ToList();
            if (keyList.Count == 0)
                throw new InvalidOperationException("No keys");

            var cmd = new StringBuilder();
            cmd.Append("DISCOVERY_EXPERIMENT_BEGIN|");
            cmd.Append(scanId);
            cmd.Append('|');
            cmd.Append(EscapePipe(label));
            for (var i = 0; i < keyList.Count; i++)
            {
                cmd.Append('|');
                cmd.Append(keyList[i]);
            }

            var resp = await IPCMeloaderClient.SendCommandAsync(cmd.ToString(), timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_EXPERIMENT_BEGIN|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            return ParseExperimentBegin(resp);
        }

        public async Task<DiscoveryExperimentEndResult> EndExperimentAsync(long experimentId, int timeoutMs = 20000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_EXPERIMENT_END|{experimentId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_EXPERIMENT_END|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            return ParseExperimentEnd(resp);
        }

        public async Task<long> StartObservationAsync(long scanId, int intervalMs, int maxSamples, IEnumerable<string> keys, int timeoutMs = 15000)
        {
            return await StartObservationAsync(scanId, intervalMs, maxSamples, null, keys, timeoutMs);
        }

        public async Task<long> StartObservationAsync(long scanId, int intervalMs, int maxSamples, IReadOnlyDictionary<string, string> options, IEnumerable<string> keys, int timeoutMs = 15000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            intervalMs = Math.Max(20, Math.Min(intervalMs, 5000));
            maxSamples = Math.Max(50, Math.Min(maxSamples, 5000));

            var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(3500).ToList();
            if (keyList.Count == 0)
                throw new InvalidOperationException("No keys");

            var cmd = new StringBuilder();
            cmd.Append("DISCOVERY_OBSERVE_START|");
            cmd.Append(scanId);
            cmd.Append('|');
            cmd.Append(intervalMs);
            cmd.Append('|');
            cmd.Append(maxSamples);
            if (options != null)
            {
                foreach (var opt in options)
                {
                    if (string.IsNullOrWhiteSpace(opt.Key))
                        continue;
                    cmd.Append('|');
                    cmd.Append(opt.Key);
                    cmd.Append('=');
                    cmd.Append(EscapePipe(opt.Value ?? ""));
                }
            }
            for (var i = 0; i < keyList.Count; i++)
            {
                cmd.Append('|');
                cmd.Append(keyList[i]);
            }

            var resp = await IPCMeloaderClient.SendCommandAsync(cmd.ToString(), timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_OBSERVE_START|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var kv = ParsePipeKeyValues(resp);
            if (!kv.TryGetValue("sessionId", out var sessionIdText) || !long.TryParse(sessionIdText, out var sessionId))
                throw new InvalidOperationException("Missing sessionId");
            return sessionId;
        }

        public async Task<DiscoveryObservationStatus> GetObservationStatusAsync(long sessionId, int timeoutMs = 12000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_OBSERVE_STATUS|{sessionId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_OBSERVE_STATUS|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var kv = ParsePipeKeyValues(resp);
            var status = new DiscoveryObservationStatus { SessionId = sessionId };
            if (kv.TryGetValue("mode", out var mode))
                status.Mode = UnescapePipe(mode);
            if (kv.TryGetValue("running", out var running))
                status.Running = running == "1";
            if (kv.TryGetValue("done", out var done))
                status.Done = done == "1";
            if (kv.TryGetValue("samples", out var samples) && long.TryParse(samples, out var s))
                status.Samples = s;
            if (kv.TryGetValue("maxSamples", out var max) && long.TryParse(max, out var ms))
                status.MaxSamples = ms;
            if (kv.TryGetValue("progress", out var progText) && double.TryParse(progText, NumberStyles.Float, CultureInfo.InvariantCulture, out var prog))
                status.Progress = prog;
            if (kv.TryGetValue("keys", out var keys) && int.TryParse(keys, out var k))
                status.Keys = k;
            return status;
        }

        public async Task<DiscoveryObservationSummaryResult> PullObservationSummaryAsync(long sessionId, int timeoutMs = 20000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_OBSERVE_SUMMARY|{sessionId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_OBSERVE_SUMMARY|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            return ParseObservationSummary(resp);
        }

        public async Task<bool> StopObservationAsync(long sessionId, int timeoutMs = 12000)
        {
            if (sessionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_OBSERVE_STOP|{sessionId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_OBSERVE_STOP|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            var kv = ParsePipeKeyValues(resp);
            return kv.TryGetValue("stopped", out var stopped) && stopped == "1";
        }

        public async Task<bool> MarkEventAsync(long sessionId, string eventName, int timeoutMs = 12000)
        {
            if (sessionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentNullException(nameof(eventName));
            var cmd = $"DISCOVERY_MARK_EVENT|{sessionId}|{EscapePipe(eventName)}";
            var resp = await IPCMeloaderClient.SendCommandAsync(cmd, timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_MARK_EVENT|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            var kv = ParsePipeKeyValues(resp);
            return kv.TryGetValue("ok", out var ok) && ok == "1";
        }

        public async Task<DiscoveryObservationPullResult> PullObservationAsync(long sessionId, long sinceSeq = 0, int maxSamples = 200, int timeoutMs = 12000)
        {
            if (sessionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            maxSamples = Math.Max(1, Math.Min(maxSamples, 800));
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_OBSERVE_PULL|{sessionId}|{sinceSeq}|{maxSamples}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_OBSERVE_PULL|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            return ParseObservationPull(resp);
        }

        public async Task<long> BindCheatAsync(long scanId, string key, string mode, IReadOnlyDictionary<string, string> args = null, int timeoutMs = 15000)
        {
            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrWhiteSpace(mode))
                throw new ArgumentNullException(nameof(mode));

            var cmd = new StringBuilder();
            cmd.Append("DISCOVERY_CHEAT_BIND|");
            cmd.Append(scanId);
            cmd.Append('|');
            cmd.Append(key);
            cmd.Append('|');
            cmd.Append(mode);
            if (args != null)
            {
                foreach (var kv in args)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;
                    cmd.Append('|');
                    cmd.Append(kv.Key);
                    cmd.Append('=');
                    cmd.Append(EscapePipe(kv.Value ?? ""));
                }
            }

            var resp = await IPCMeloaderClient.SendCommandAsync(cmd.ToString(), timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_CHEAT_BIND|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var dict = ParsePipeKeyValues(resp);
            if (!dict.TryGetValue("id", out var idText) || !long.TryParse(idText, out var id))
                throw new InvalidOperationException("Missing cheat id");
            return id;
        }

        public async Task<bool> UnbindCheatAsync(long cheatId, int timeoutMs = 15000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync($"DISCOVERY_CHEAT_UNBIND|{cheatId}", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_CHEAT_UNBIND|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");
            var dict = ParsePipeKeyValues(resp);
            return dict.TryGetValue("removed", out var r) && r == "1";
        }

        public async Task<DiscoveryCheatListResult> ListCheatsAsync(int timeoutMs = 15000)
        {
            var resp = await IPCMeloaderClient.SendCommandAsync("DISCOVERY_CHEAT_LIST", timeoutMs);
            if (IPCMeloaderClient.TryParseError(resp, out var err))
                throw new InvalidOperationException(err);
            if (resp == null || !resp.StartsWith("DISCOVERY_CHEAT_LIST|", StringComparison.Ordinal))
                throw new InvalidOperationException(resp ?? "No response");

            var parts = resp.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var result = new DiscoveryCheatListResult();
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("c=", StringComparison.Ordinal))
                {
                    var payload = p.Substring("c=".Length);
                    if (payload.StartsWith("id:", StringComparison.Ordinal))
                        payload = payload.Substring("id:".Length);
                    var segs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (segs.Length > 0)
                        dict["id"] = segs[0];
                    for (var s = 1; s < segs.Length; s++)
                    {
                        var idx = segs[s].IndexOf(':');
                        if (idx <= 0)
                            continue;
                        dict[segs[s].Substring(0, idx)] = segs[s].Substring(idx + 1);
                    }

                    var info = new DiscoveryCheatInfo();
                    if (dict.TryGetValue("id", out var idText) && long.TryParse(idText, out var id))
                        info.CheatId = id;
                    if (dict.TryGetValue("mode", out var modeText) && int.TryParse(modeText, out var mode))
                        info.Mode = mode;
                    if (dict.TryGetValue("kind", out var kindText) && int.TryParse(kindText, out var kind))
                        info.Kind = (DiscoveryCandidateKind)kind;
                    if (dict.TryGetValue("key", out var key))
                        info.Key = UnescapePipe(key);
                    if (dict.TryGetValue("fp", out var fp))
                        info.Fingerprint = UnescapePipe(fp);
                    result.Cheats.Add(info);
                }
            }
            return result;
        }

        private static DiscoveryObservationPullResult ParseObservationPull(string response)
        {
            var result = new DiscoveryObservationPullResult();
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("sessionId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("sessionId=".Length), out var sid);
                    result.SessionId = sid;
                    continue;
                }
                if (p.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("ticks=".Length), out var ticks);
                    result.Ticks = ticks;
                    continue;
                }
                if (p.StartsWith("e=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Events.Add(UnescapePipe(p.Substring("e=".Length)));
                    continue;
                }
                if (p.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = p.Substring("s=".Length);
                    var segs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var sample = new DiscoveryObservationSample();
                    for (var si = 0; si < segs.Length; si++)
                    {
                        var seg = segs[si];
                        if (seg.StartsWith("seq:", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(seg.Substring("seq:".Length), out var seq);
                            sample.Seq = seq;
                            continue;
                        }
                        if (seg.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(seg.Substring("t:".Length), out var ticks);
                            sample.Ticks = ticks;
                            continue;
                        }
                        if (seg.StartsWith("k:", StringComparison.OrdinalIgnoreCase))
                        {
                            var comma = seg.IndexOf(",v:", StringComparison.OrdinalIgnoreCase);
                            if (comma <= 2)
                                continue;
                            var key = UnescapePipe(seg.Substring("k:".Length, comma - "k:".Length));
                            var v = UnescapePipe(seg.Substring(comma + ",v:".Length));
                            if (!string.IsNullOrWhiteSpace(key))
                                sample.Values[key] = v;
                            continue;
                        }
                    }
                    if (sample.Seq > 0)
                        result.Samples.Add(sample);
                    continue;
                }
                if (p.StartsWith("lastSeq=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("lastSeq=".Length), out var ls);
                    result.LastSeq = ls;
                }
            }
            return result;
        }

        private static DiscoveryObservationSummaryResult ParseObservationSummary(string response)
        {
            var result = new DiscoveryObservationSummaryResult();
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("sessionId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("sessionId=".Length), out var sid);
                    result.SessionId = sid;
                    continue;
                }
                if (p.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("ticks=".Length), out var ticks);
                    result.Ticks = ticks;
                    continue;
                }
                if (p.StartsWith("samples=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("samples=".Length), out var s);
                    result.Samples = s;
                    continue;
                }
                if (p.StartsWith("keys=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(p.Substring("keys=".Length), out var k);
                    result.Keys = k;
                    continue;
                }
                if (p.StartsWith("done=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Done = p.Substring("done=".Length) == "1";
                    continue;
                }
                if (p.StartsWith("k=", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = ParseSemicolonKeyValues(p);
                    if (!dict.TryGetValue("k", out var key) || string.IsNullOrWhiteSpace(key))
                        continue;

                    var entry = new DiscoveryObservationSummaryEntry
                    {
                        Key = UnescapePipe(key)
                    };
                    if (dict.TryGetValue("n", out var nText) && long.TryParse(nText, out var n))
                        entry.N = n;
                    if (dict.TryGetValue("min", out var minText) && double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mn))
                        entry.Min = mn;
                    if (dict.TryGetValue("max", out var maxText) && double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mx))
                        entry.Max = mx;
                    if (dict.TryGetValue("mean", out var meanText) && double.TryParse(meanText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mean))
                        entry.Mean = mean;
                    if (dict.TryGetValue("var", out var varText) && double.TryParse(varText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        entry.Variance = v;
                    if (dict.TryGetValue("upd", out var updText) && double.TryParse(updText, NumberStyles.Float, CultureInfo.InvariantCulture, out var upd))
                        entry.UpdateRate = upd;
                    if (dict.TryGetValue("int", out var intText) && double.TryParse(intText, NumberStyles.Float, CultureInfo.InvariantCulture, out var ir))
                        entry.IntegerRate = ir;
                    result.Entries.Add(entry);
                }
            }
            return result;
        }

        private static void ParseScanPage(string response, DiscoveryScan scan, out int offset, out int total, out int count)
        {
            offset = 0;
            total = 0;
            count = 0;

            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("scanId=", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.StartsWith("offset=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(p.Substring("offset=".Length), out offset);
                    continue;
                }
                if (p.StartsWith("total=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(p.Substring("total=".Length), out total);
                    continue;
                }
                if (!p.StartsWith("k=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dict = ParseSemicolonKeyValues(p);
                if (!dict.TryGetValue("k", out var key) || string.IsNullOrWhiteSpace(key))
                    continue;

                var candidate = new DiscoveryCandidate
                {
                    Key = UnescapePipe(key),
                    Fingerprint = dict.TryGetValue("fp", out var fp) ? UnescapePipe(fp) : "",
                    Kind = dict.TryGetValue("kind", out var kindText) && int.TryParse(kindText, out var kind) ? (DiscoveryCandidateKind)kind : DiscoveryCandidateKind.Numeric,
                    GameObjectName = dict.TryGetValue("go", out var go) ? UnescapePipe(go) : "",
                    GameObjectPath = dict.TryGetValue("path", out var path) ? UnescapePipe(path) : "",
                    ComponentTypeName = dict.TryGetValue("comp", out var comp) ? UnescapePipe(comp) : "",
                    DeclaringTypeName = dict.TryGetValue("decl", out var decl) ? UnescapePipe(decl) : "",
                    MemberName = dict.TryGetValue("mem", out var mem) ? UnescapePipe(mem) : "",
                    MemberTypeName = dict.TryGetValue("mt", out var mt) ? UnescapePipe(mt) : "",
                    CanWrite = dict.TryGetValue("rw", out var rw) && rw == "1",
                    IsStatic = dict.TryGetValue("st", out var st) && st == "1",
                    SourceScanId = scan.ScanId
                };

                scan.Candidates.Add(candidate);
                scan.ByKey[candidate.Key] = candidate;
                if (!string.IsNullOrWhiteSpace(candidate.Fingerprint))
                    scan.ByFingerprint[candidate.Fingerprint] = candidate;
                count++;
            }
        }

        private static DiscoverySnapshot ParseSnapshot(string response)
        {
            var snap = new DiscoverySnapshot();
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("scanId=", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(p.Substring("scanId=".Length), out var scanId))
                        snap.ScanId = scanId;
                    continue;
                }
                if (p.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(p.Substring("ticks=".Length), out var ticks))
                        snap.Ticks = ticks;
                    continue;
                }
                if (p.StartsWith("k=", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = ParseSemicolonKeyValues(p);
                    if (dict.TryGetValue("k", out var key) && dict.TryGetValue("v", out var v))
                        snap.Values[UnescapePipe(key)] = UnescapePipe(v);
                }
            }
            return snap;
        }

        private static DiscoveryExperimentBeginResult ParseExperimentBegin(string response)
        {
            var result = new DiscoveryExperimentBeginResult();
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("expId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("expId=".Length), out var expId);
                    result.ExperimentId = expId;
                    continue;
                }
                if (p.StartsWith("scanId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("scanId=".Length), out var scanId);
                    result.ScanId = scanId;
                    continue;
                }
                if (p.StartsWith("label=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Label = UnescapePipe(p.Substring("label=".Length));
                    continue;
                }
                if (p.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("ticks=".Length), out var ticks);
                    result.Ticks = ticks;
                    continue;
                }
                if (p.StartsWith("k=", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = ParseSemicolonKeyValues(p);
                    if (dict.TryGetValue("k", out var key) && dict.TryGetValue("v", out var v))
                        result.BeforeValues[UnescapePipe(key)] = UnescapePipe(v);
                }
            }
            return result;
        }

        private static DiscoveryExperimentEndResult ParseExperimentEnd(string response)
        {
            var result = new DiscoveryExperimentEndResult();
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.StartsWith("expId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("expId=".Length), out var expId);
                    result.ExperimentId = expId;
                    continue;
                }
                if (p.StartsWith("scanId=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("scanId=".Length), out var scanId);
                    result.ScanId = scanId;
                    continue;
                }
                if (p.StartsWith("label=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Label = UnescapePipe(p.Substring("label=".Length));
                    continue;
                }
                if (p.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(p.Substring("ticks=".Length), out var ticks);
                    result.Ticks = ticks;
                    continue;
                }
                if (p.StartsWith("changed=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(p.Substring("changed=".Length), out var changed);
                    result.Changed = changed;
                    continue;
                }
                if (p.StartsWith("unchanged=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(p.Substring("unchanged=".Length), out var unchanged);
                    result.Unchanged = unchanged;
                    continue;
                }
                if (p.StartsWith("c=", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = p.Substring("c=".Length);
                    var dict = ParseSemicolonKeyValues(payload);
                    if (!dict.TryGetValue("k", out var key))
                        continue;

                    var d = new DiscoveryExperimentDelta
                    {
                        Key = UnescapePipe(key),
                        Kind = dict.TryGetValue("kind", out var kindText) && int.TryParse(kindText, out var kind) ? (DiscoveryCandidateKind)kind : DiscoveryCandidateKind.Numeric,
                        Before = dict.TryGetValue("before", out var before) ? UnescapePipe(before) : null,
                        After = dict.TryGetValue("after", out var after) ? UnescapePipe(after) : null
                    };

                    if (dict.TryGetValue("delta", out var deltaText) && double.TryParse(deltaText, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                        d.Delta = delta;
                    result.Deltas.Add(d);
                }
            }
            return result;
        }

        private static DiscoveryWriteProbeResult ParseWriteProbe(string response)
        {
            var kv = ParsePipeKeyValues(response);
            var result = new DiscoveryWriteProbeResult();

            if (kv.TryGetValue("scanId", out var scanIdText) && long.TryParse(scanIdText, out var scanId))
                result.ScanId = scanId;
            if (kv.TryGetValue("key", out var key))
                result.Key = UnescapePipe(key);
            if (kv.TryGetValue("fp", out var fp))
                result.Fingerprint = UnescapePipe(fp);
            if (kv.TryGetValue("kind", out var kindText) && int.TryParse(kindText, out var kind))
                result.Kind = (DiscoveryCandidateKind)kind;
            if (kv.TryGetValue("delayMs", out var delayText) && int.TryParse(delayText, out var delay))
                result.DelayMs = delay;
            if (kv.TryGetValue("wrote", out var wrote))
                result.Wrote = wrote == "1";
            if (kv.TryGetValue("reverted", out var reverted))
                result.Reverted = reverted == "1";
            if (kv.TryGetValue("sticky", out var sticky))
                result.Sticky = sticky == "1";
            if (kv.TryGetValue("rubber", out var rubber))
                result.RubberBand = rubber == "1";
            if (kv.TryGetValue("clamp", out var clamp))
                result.ClampDetected = clamp == "1";
            if (kv.TryGetValue("score", out var scoreText) && double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                result.Score = score;
            if (kv.TryGetValue("before", out var before))
                result.Before = UnescapePipe(before);
            if (kv.TryGetValue("after0", out var after0))
                result.After0 = UnescapePipe(after0);
            if (kv.TryGetValue("after", out var after))
                result.After = UnescapePipe(after);
            if (kv.TryGetValue("afterRevert", out var afterRevert))
                result.AfterRevert = UnescapePipe(afterRevert);
            if (kv.TryGetValue("probe", out var probe))
                result.Probe = UnescapePipe(probe);

            return result;
        }

        private static Dictionary<string, string> ParseSemicolonKeyValues(string token)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(token))
                return dict;
            var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                dict[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            return dict;
        }

        private static Dictionary<string, string> ParsePipeKeyValues(string response)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(response))
                return dict;
            var parts = response.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                dict[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            return dict;
        }

        private static string EscapePipe(string value)
        {
            if (value == null)
                return "";
            return value.Replace("|", "%7C").Replace(";", "%3B");
        }

        private static string UnescapePipe(string value)
        {
            if (value == null)
                return "";
            return value.Replace("%7C", "|").Replace("%3B", ";");
        }
    }

    public static class DiscoveryScanEnsembler
    {
        private sealed class Accum
        {
            public DiscoveryCandidate Best;
            public int Seen;
            public long BestScanId;
        }

        public static DiscoveryScan Merge(IReadOnlyList<DiscoveryScan> scans, long preferredPrimaryScanId = 0)
        {
            if (scans == null || scans.Count == 0)
                return new DiscoveryScan { ScanId = 0, CreatedUtc = DateTime.UtcNow };

            var primary = preferredPrimaryScanId > 0 ? preferredPrimaryScanId : scans.Max(s => s?.ScanId ?? 0);
            var total = scans.Count(s => s != null);
            if (total <= 0)
                total = scans.Count;

            var byFp = new Dictionary<string, Accum>(StringComparer.Ordinal);

            for (var si = 0; si < scans.Count; si++)
            {
                var scan = scans[si];
                if (scan?.Candidates == null)
                    continue;
                var scanId = scan.ScanId;

                for (var i = 0; i < scan.Candidates.Count; i++)
                {
                    var c = scan.Candidates[i];
                    if (c == null || string.IsNullOrWhiteSpace(c.Key))
                        continue;
                    if (c.SourceScanId == 0)
                        c.SourceScanId = scanId;

                    var fp = string.IsNullOrWhiteSpace(c.Fingerprint) ? c.Key : c.Fingerprint;
                    if (!byFp.TryGetValue(fp, out var a))
                    {
                        a = new Accum { Best = c, Seen = 0, BestScanId = c.SourceScanId };
                        byFp[fp] = a;
                    }

                    a.Seen++;
                    if (IsBetterCandidate(c, a.Best, a.BestScanId))
                    {
                        a.Best = c;
                        a.BestScanId = c.SourceScanId;
                    }
                    else if (c.SourceScanId > a.BestScanId && IsComparableCandidate(c, a.Best))
                    {
                        a.Best = c;
                        a.BestScanId = c.SourceScanId;
                    }
                }
            }

            var merged = new DiscoveryScan { ScanId = primary, CreatedUtc = DateTime.UtcNow };

            foreach (var kv in byFp)
            {
                var a = kv.Value;
                if (a?.Best == null)
                    continue;

                var best = a.Best;
                best.SeenInScans = a.Seen;
                best.TotalScans = total;
                best.PresenceRate = total > 0 ? (a.Seen / (double)total) : 0;

                merged.Candidates.Add(best);
                if (!string.IsNullOrWhiteSpace(best.Key))
                    merged.ByKey[best.Key] = best;
                if (!string.IsNullOrWhiteSpace(best.Fingerprint))
                    merged.ByFingerprint[best.Fingerprint] = best;
            }

            return merged;
        }

        private static bool IsComparableCandidate(DiscoveryCandidate a, DiscoveryCandidate b)
        {
            if (a == null || b == null)
                return false;
            if (!string.Equals(a.MemberName ?? "", b.MemberName ?? "", StringComparison.Ordinal))
                return false;
            if (!string.Equals(a.DeclaringTypeName ?? "", b.DeclaringTypeName ?? "", StringComparison.Ordinal))
                return false;
            if (!string.Equals(a.ComponentTypeName ?? "", b.ComponentTypeName ?? "", StringComparison.Ordinal))
                return false;
            return true;
        }

        private static bool IsBetterCandidate(DiscoveryCandidate candidate, DiscoveryCandidate currentBest, long currentBestScanId)
        {
            if (candidate == null)
                return false;
            if (currentBest == null)
                return true;

            var cScore = 0;
            var bScore = 0;

            if (candidate.CanWrite) cScore += 6;
            if (currentBest.CanWrite) bScore += 6;

            if (!candidate.IsStatic) cScore += 2;
            if (!currentBest.IsStatic) bScore += 2;

            var cPath = (candidate.GameObjectPath ?? "").Length;
            var bPath = (currentBest.GameObjectPath ?? "").Length;
            if (cPath > 0) cScore += 2;
            if (bPath > 0) bScore += 2;
            if (cPath > 0 && bPath > 0)
            {
                if (cPath < bPath) cScore += 1;
                else if (bPath < cPath) bScore += 1;
            }

            if (!string.IsNullOrWhiteSpace(candidate.MemberName)) cScore += 1;
            if (!string.IsNullOrWhiteSpace(currentBest.MemberName)) bScore += 1;

            if (!string.IsNullOrWhiteSpace(candidate.MemberTypeName)) cScore += 1;
            if (!string.IsNullOrWhiteSpace(currentBest.MemberTypeName)) bScore += 1;

            if (cScore != bScore)
                return cScore > bScore;

            if (candidate.SourceScanId > currentBestScanId)
                return true;

            return false;
        }
    }

    public sealed class HeuristicDiscoveryEngine
    {
        public IReadOnlyList<DiscoveryCandidateScore> RankCandidates(DiscoveryScan scan, DiscoveryConcept concept, int top = 250)
        {
            return RankCandidates(scan, concept, null, null, null, top);
        }

        public IReadOnlyList<DiscoveryCandidateScore> RankCandidates(DiscoveryScan scan, DiscoveryConcept concept, IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures, IReadOnlyDictionary<string, string> currentToMaxKey, int top = 250)
        {
             return RankCandidates(scan, concept, signatures, currentToMaxKey, null, top);
        }

        public IReadOnlyList<DiscoveryCandidateScore> RankCandidates(DiscoveryScan scan, DiscoveryConcept concept, IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures, IReadOnlyDictionary<string, string> currentToMaxKey, DiscoveryKnowledgeBase kb, int top = 250)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));

            var rows = new List<DiscoveryCandidateScore>(Math.Min(top, 512));
            foreach (var c in scan.Candidates)
            {
                if (c == null)
                    continue;
                DiscoveryBehaviorSignature sig = null;
                if (signatures != null && !string.IsNullOrWhiteSpace(c.Fingerprint))
                    signatures.TryGetValue(c.Fingerprint, out sig);
                var score = ScoreCandidate(c, concept, sig, currentToMaxKey, kb);
                if (score.Score <= 0)
                    continue;
                rows.Add(score);
            }

            return rows
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Candidate.MemberName, StringComparer.OrdinalIgnoreCase)
                .Take(top)
                .ToList();
        }

        public IReadOnlyList<DiscoveryCandidateScore> RankCheatCandidates(
            DiscoveryScan scan,
            DiscoveryConcept concept,
            IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures,
            IReadOnlyDictionary<string, string> currentToMaxKey,
            DiscoveryKnowledgeBase kb,
            int top = 250)
        {
            var ranked = RankCandidates(scan, concept, signatures, currentToMaxKey, kb, Math.Max(350, top * 3));
            if (ranked == null || ranked.Count == 0)
                return Array.Empty<DiscoveryCandidateScore>();

            var hasCausalPrior = kb?.CausalPriors != null && kb.CausalPriors.Any(p => p != null && p.Concept == concept);
            var filtered = new List<DiscoveryCandidateScore>(Math.Min(ranked.Count, top));

            for (var i = 0; i < ranked.Count; i++)
            {
                var r = ranked[i];
                var cand = r?.Candidate;
                if (cand == null)
                    continue;

                DiscoveryBehaviorSignature sig = null;
                if (signatures != null && !string.IsNullOrWhiteSpace(cand.Fingerprint))
                    signatures.TryGetValue(cand.Fingerprint, out sig);

                if (!IsEligibleCheatCandidate(cand, concept, sig))
                    continue;
                if (!PassConceptThresholds(concept, r, sig, hasCausalPrior))
                    continue;

                filtered.Add(r);
                if (filtered.Count >= top)
                    break;
            }

            return filtered;
        }

        public bool TryResolveConfirmedMapping(DiscoveryScan scan, DiscoveryConcept concept, DiscoveryKnowledgeBase kb, out DiscoveryCandidate candidate)
        {
            candidate = null;
            if (scan == null || kb?.ConfirmedMappings == null)
                return false;

            for (var i = 0; i < kb.ConfirmedMappings.Count; i++)
            {
                var m = kb.ConfirmedMappings[i];
                if (m == null || m.Concept != concept || string.IsNullOrWhiteSpace(m.Fingerprint))
                    continue;
                if (scan.ByFingerprint.TryGetValue(m.Fingerprint, out var c) && c != null)
                {
                    candidate = c;
                    return true;
                }
            }

            return false;
        }

        private static bool PassConceptThresholds(DiscoveryConcept concept, DiscoveryCandidateScore r, DiscoveryBehaviorSignature sig, bool hasCausalPrior)
        {
            if (r == null)
                return false;

            var total = r.Score;
            var name = r.NameScore;
            var type = r.TypeScore;
            var ctx = r.ContextScore;
            var dyn = r.DynamicScore;
            var db = r.DatabaseScore;
            var kb = r.KnowledgeScore;
            var causal = r.CausalScore;

            var minTotal = concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.StaminaCurrent => 0.62,
                DiscoveryConcept.AmmoCurrent => 0.60,
                DiscoveryConcept.Currency or DiscoveryConcept.Score => 0.56,
                DiscoveryConcept.Cooldown or DiscoveryConcept.AbilityCharges => 0.60,
                DiscoveryConcept.InventoryCount => 0.55,
                DiscoveryConcept.HealthMax or DiscoveryConcept.AmmoMax or DiscoveryConcept.StaminaMax => 0.56,
                DiscoveryConcept.ShieldCurrent => 0.60,
                DiscoveryConcept.ShieldMax => 0.55,
                DiscoveryConcept.Lives => 0.55,
                _ => 0.58
            };

            if (kb >= 0.95)
                minTotal = Math.Min(minTotal, 0.40);

            if (hasCausalPrior && kb < 0.95 && causal < 0.05)
                minTotal = Math.Max(minTotal, 0.66);

            if (total < minTotal)
                return false;

            var nameOk = name >= 0.35 || db >= 0.45;
            if (!nameOk && ctx < 0.45)
                return false;

            if (concept == DiscoveryConcept.Currency || concept == DiscoveryConcept.Score || concept == DiscoveryConcept.InventoryCount || concept == DiscoveryConcept.Lives)
            {
                if (type < 0.60 && (sig == null || !sig.MostlyInteger))
                    return false;
            }

            if (concept == DiscoveryConcept.Cooldown)
            {
                if (sig != null)
                {
                    if (sig.Min != null && sig.Min.Value < -1e-3)
                        return false;
                    if (sig.UpdateRate > 0.9)
                        return false;
                }
                if (dyn < 0.20 && name < 0.50 && db < 0.50)
                    return false;
            }

            if (sig != null)
            {
                if (sig.Max != null && sig.Min != null)
                {
                    var range = sig.Max.Value - sig.Min.Value;
                    if (range <= 1e-6 && sig.UpdateRate <= 0.01)
                        return false;
                }
                if (sig.UpdateRate > 1.2)
                    return false;
            }

            return true;
        }

        private static bool IsEligibleCheatCandidate(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryBehaviorSignature sig)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Key))
                return false;
            if (!c.CanWrite && concept != DiscoveryConcept.InventoryCount)
                return false;
            if (c.IsStatic)
                return false;
            if (c.Kind != DiscoveryCandidateKind.Numeric && c.Kind != DiscoveryCandidateKind.CollectionCount)
                return false;
            if (string.IsNullOrWhiteSpace(c.MemberName) || c.MemberName.Length < 2)
                return false;

            if (StartsWithAny(c.ComponentTypeName ?? "", "UnityEngine.", "TMPro.", "UnityEngine.UI", "Il2CppSystem.", "System.") ||
                StartsWithAny(c.DeclaringTypeName ?? "", "UnityEngine.", "TMPro.", "UnityEngine.UI", "Il2CppSystem.", "System."))
                return false;

            if (IsLikelyUiCandidate(c))
                return false;

            var noisePenalty = ScoreNoisePenalty(c, concept);
            if (noisePenalty < 0.55)
                return false;

            var hay = (c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.MemberName ?? "") + " " + (c.GameObjectPath ?? "") + " " + (c.GameObjectName ?? "");
            var tokens = Tokenize(hay);
            if (AnyTokenLike(tokens, new[] { "setting", "settings", "config", "configuration", "option", "options", "prefs", "preference", "playerprefs" }))
                return false;
            if (AnyTokenLike(tokens, new[] { "graphics", "render", "renderer", "shader", "postprocess", "quality", "resolution", "vsync", "fps", "antia", "texture" }))
                return false;
            if (AnyTokenLike(tokens, new[] { "audio", "sound", "music", "sfx", "volume", "mute" }))
                return false;
            if (AnyTokenLike(tokens, new[] { "language", "locale", "localization", "subtitle" }))
                return false;
            if (AnyTokenLike(tokens, new[] { "debug", "test", "demo", "example", "editor" }))
                return false;

            if (sig != null)
            {
                if (sig.Max != null && sig.Min != null)
                {
                    var range = sig.Max.Value - sig.Min.Value;
                    if (range <= 1e-6 && sig.UpdateRate <= 0.01)
                        return false;
                }
                if (sig.UpdateRate > 1.5)
                    return false;
            }

            return true;
        }

        public DiscoveryExperimentSuggestion SuggestNextExperiment(DiscoveryConcept concept, IReadOnlyList<DiscoveryCandidateScore> ranking, double minTopScore = 0.62, double minGap = 0.06)
        {
            if (concept != DiscoveryConcept.HealthCurrent &&
                concept != DiscoveryConcept.AmmoCurrent &&
                concept != DiscoveryConcept.StaminaCurrent &&
                concept != DiscoveryConcept.Currency &&
                concept != DiscoveryConcept.Cooldown)
                return null;

            var topScore = (ranking != null && ranking.Count > 0) ? ranking[0].Score : 0;
            var topCausal = (ranking != null && ranking.Count > 0) ? ranking[0].CausalScore : 0;
            var gap = (ranking != null && ranking.Count > 1) ? (ranking[0].Score - ranking[1].Score) : 0;

            var ambiguous = ranking == null || ranking.Count < 2 || topScore < minTopScore || gap < minGap || topCausal < 0.02;
            if (!ambiguous)
                return null;

            if (concept == DiscoveryConcept.HealthCurrent)
                return new DiscoveryExperimentSuggestion { Concept = concept, Label = "damage_test", EventName = "damage_test", ObserveSeconds = 8, Prompt = "Suggest: Run Damage Test (take damage once, then wait ~8s)" };
            if (concept == DiscoveryConcept.AmmoCurrent)
                return new DiscoveryExperimentSuggestion { Concept = concept, Label = "ammo_test", EventName = "ammo_test", ObserveSeconds = 8, Prompt = "Suggest: Run Ammo Test (fire/reload once, then wait ~8s)" };
            if (concept == DiscoveryConcept.StaminaCurrent)
                return new DiscoveryExperimentSuggestion { Concept = concept, Label = "sprint_test", EventName = "sprint_test", ObserveSeconds = 8, Prompt = "Suggest: Run Sprint Test (sprint briefly, then wait ~8s)" };
            if (concept == DiscoveryConcept.Currency)
                return new DiscoveryExperimentSuggestion { Concept = concept, Label = "buy_test", EventName = "buy_test", ObserveSeconds = 10, Prompt = "Suggest: Run Buy Test (buy/spend once, then wait ~10s)" };
            if (concept == DiscoveryConcept.Cooldown)
                return new DiscoveryExperimentSuggestion { Concept = concept, Label = "cooldown_test", EventName = "cooldown_test", ObserveSeconds = 12, Prompt = "Suggest: Run Cooldown Test (trigger ability once, then wait ~12s)" };

            return null;
        }

        public IReadOnlyList<DiscoveryCandidateScore> ApplyEventCausalEvidence(
            DiscoveryScan scan,
            DiscoveryConcept concept,
            IReadOnlyList<DiscoveryCandidateScore> baseRanking,
            IReadOnlyList<DiscoveryObservationSample> samples,
            IReadOnlyList<string> events,
            int top = 250)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (baseRanking == null)
                throw new ArgumentNullException(nameof(baseRanking));
            if (samples == null || samples.Count == 0 || events == null || events.Count == 0)
                return baseRanking.Take(top).ToList();

            var eventTokens = concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.HealthMax or DiscoveryConcept.ShieldCurrent or DiscoveryConcept.ShieldMax or DiscoveryConcept.Lives => new[] { "damage", "hurt", "hit" },
                DiscoveryConcept.AmmoCurrent or DiscoveryConcept.AmmoMax => new[] { "ammo", "shoot", "fire", "reload" },
                DiscoveryConcept.StaminaCurrent or DiscoveryConcept.StaminaMax => new[] { "sprint", "run", "stamina" },
                DiscoveryConcept.Currency or DiscoveryConcept.Score => new[] { "buy", "spend", "shop", "money", "reward", "score" },
                DiscoveryConcept.Cooldown or DiscoveryConcept.AbilityCharges => new[] { "cooldown", "ability", "skill", "cast", "charge" },
                DiscoveryConcept.InventoryCount => new[] { "pickup", "loot", "inventory", "item" },
                _ => Array.Empty<string>()
            };

            var parsedEvents = new List<(long Ticks, string Name)>(events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (TryParseEvent(e, out var t, out var name))
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        parsedEvents.Add((t, name));
                }
            }
            if (parsedEvents.Count == 0)
                return baseRanking.Take(top).ToList();

            var candidateKeys = baseRanking
                .Select(r => r?.Candidate?.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidateKeys.Count == 0)
                return baseRanking.Take(top).ToList();

            var preWindow = (long)(Stopwatch.Frequency * 1.0);
            var postWindow = (long)(Stopwatch.Frequency * 2.5);

            var direction = concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.AmmoCurrent or DiscoveryConcept.StaminaCurrent => -1,
                DiscoveryConcept.Cooldown => 1,
                _ => 0
            };

            var bonusByKey = new Dictionary<string, double>(StringComparer.Ordinal);

            for (var ei = 0; ei < parsedEvents.Count; ei++)
            {
                var (t0, name) = parsedEvents[ei];
                if (t0 <= 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                if (eventTokens.Length > 0)
                {
                    var lower = name.ToLowerInvariant();
                    var relevant = false;
                    for (var ti = 0; ti < eventTokens.Length; ti++)
                    {
                        if (lower.Contains(eventTokens[ti], StringComparison.Ordinal))
                        {
                            relevant = true;
                            break;
                        }
                    }
                    if (!relevant)
                        continue;
                }

                var preSample = FindPreSample(samples, t0, preWindow);
                if (preSample?.Values == null || preSample.Values.Count == 0)
                    continue;

                var afterSamples = SliceAfterSamples(samples, t0, postWindow);
                if (afterSamples.Count == 0)
                    continue;

                for (var ki = 0; ki < candidateKeys.Count; ki++)
                {
                    var key = candidateKeys[ki];
                    if (!TryGetNumeric(preSample, key, out var pre))
                        continue;

                    var hasBest = false;
                    var bestDelta = 0d;

                    for (var si = 0; si < afterSamples.Count; si++)
                    {
                        var s = afterSamples[si];
                        if (!TryGetNumeric(s, key, out var post))
                            continue;
                        var d = post - pre;
                        if (!hasBest)
                        {
                            bestDelta = d;
                            hasBest = true;
                            continue;
                        }

                        if (direction < 0)
                        {
                            if (d < bestDelta)
                                bestDelta = d;
                        }
                        else if (direction > 0)
                        {
                            if (d > bestDelta)
                                bestDelta = d;
                        }
                        else
                        {
                            if (Math.Abs(d) > Math.Abs(bestDelta))
                                bestDelta = d;
                        }
                    }

                    if (!hasBest)
                        continue;

                    if (direction < 0 && bestDelta >= -1e-9)
                        continue;
                    if (direction > 0 && bestDelta <= 1e-9)
                        continue;

                    var magnitude = Math.Abs(bestDelta);
                    if (magnitude <= 1e-6)
                        continue;

                    var scale = (Math.Abs(pre) * 0.05) + 1.0;
                    var norm = Math.Min(1.0, magnitude / scale);
                    var bonus = norm * 0.25;

                    if (!bonusByKey.TryGetValue(key, out var existing))
                        existing = 0;
                    bonusByKey[key] = Math.Min(0.30, existing + bonus);
                }
            }

            if (bonusByKey.Count == 0)
                return baseRanking.Take(top).ToList();

            var adjusted = new List<DiscoveryCandidateScore>(baseRanking.Count);
            for (var i = 0; i < baseRanking.Count; i++)
            {
                var r = baseRanking[i];
                if (r?.Candidate?.Key == null)
                    continue;

                bonusByKey.TryGetValue(r.Candidate.Key, out var b);

                adjusted.Add(new DiscoveryCandidateScore
                {
                    Candidate = r.Candidate,
                    Concept = r.Concept,
                    NameScore = r.NameScore,
                    TypeScore = r.TypeScore,
                    ContextScore = r.ContextScore,
                    DynamicScore = r.DynamicScore,
                    DatabaseScore = r.DatabaseScore,
                    KnowledgeScore = r.KnowledgeScore,
                    RelationScore = r.RelationScore,
                    CausalScore = r.CausalScore + b,
                    Score = r.Score + b
                });
            }

            return adjusted
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Candidate.MemberName, StringComparer.OrdinalIgnoreCase)
                .Take(top)
                .ToList();
        }

        private static bool TryParseEvent(string payload, out long ticks, out string name)
        {
            ticks = 0;
            name = null;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var segs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segs.Length; i++)
            {
                var seg = segs[i];
                var eq = seg.IndexOf('=');
                if (eq <= 0)
                    continue;
                var k = seg.Substring(0, eq);
                var v = seg.Substring(eq + 1);
                if (k.Equals("t", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(v, out ticks);
                    continue;
                }
                if (k.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    name = v;
                    continue;
                }
            }

            return ticks > 0 && !string.IsNullOrWhiteSpace(name);
        }

        private static DiscoveryObservationSample FindPreSample(IReadOnlyList<DiscoveryObservationSample> samples, long t0, long preWindow)
        {
            for (var i = samples.Count - 1; i >= 0; i--)
            {
                var s = samples[i];
                if (s == null)
                    continue;
                if (s.Ticks >= t0)
                    continue;
                if (s.Ticks < t0 - preWindow)
                    break;
                return s;
            }
            return null;
        }

        private static List<DiscoveryObservationSample> SliceAfterSamples(IReadOnlyList<DiscoveryObservationSample> samples, long t0, long postWindow)
        {
            var list = new List<DiscoveryObservationSample>();
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s == null)
                    continue;
                if (s.Ticks <= t0)
                    continue;
                if (s.Ticks > t0 + postWindow)
                    break;
                list.Add(s);
                if (list.Count >= 600)
                    break;
            }
            return list;
        }

        private static bool TryGetNumeric(DiscoveryObservationSample sample, string key, out double value)
        {
            value = 0;
            if (sample?.Values == null || string.IsNullOrWhiteSpace(key))
                return false;
            if (!sample.Values.TryGetValue(key, out var text))
                return false;
            return double.TryParse(text ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public Dictionary<string, DiscoveryBehaviorSignature> ComputeBehaviorSignatures(DiscoveryScan scan, IReadOnlyList<DiscoveryObservationSample> samples)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (samples == null || samples.Count == 0)
                return new Dictionary<string, DiscoveryBehaviorSignature>(StringComparer.Ordinal);

            var byKey = new Dictionary<string, DiscoveryCandidate>(StringComparer.Ordinal);
            foreach (var c in scan.Candidates)
                if (c != null && !string.IsNullOrWhiteSpace(c.Key))
                    byKey[c.Key] = c;

            var accum = new Dictionary<string, OnlineStats>(StringComparer.Ordinal);
            var ints = new Dictionary<string, int>(StringComparer.Ordinal);
            var nums = new Dictionary<string, int>(StringComparer.Ordinal);
            var updates = new Dictionary<string, int>(StringComparer.Ordinal);
            var last = new Dictionary<string, double>(StringComparer.Ordinal);
            var lastSet = new HashSet<string>(StringComparer.Ordinal);

            for (var si = 0; si < samples.Count; si++)
            {
                var s = samples[si];
                if (s?.Values == null)
                    continue;

                foreach (var kv in s.Values)
                {
                    if (!byKey.TryGetValue(kv.Key, out var cand) || cand == null)
                        continue;
                    if (cand.Kind != DiscoveryCandidateKind.Numeric && cand.Kind != DiscoveryCandidateKind.CollectionCount)
                        continue;
                    if (!double.TryParse(kv.Value ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        continue;

                    if (!accum.TryGetValue(kv.Key, out var st))
                        st = new OnlineStats();
                    st.Push(v);
                    accum[kv.Key] = st;

                    nums[kv.Key] = nums.TryGetValue(kv.Key, out var n) ? (n + 1) : 1;
                    if (Math.Abs(v - Math.Round(v)) < 1e-6)
                        ints[kv.Key] = ints.TryGetValue(kv.Key, out var ic) ? (ic + 1) : 1;

                    if (lastSet.Contains(kv.Key))
                    {
                        var prev = last[kv.Key];
                        if (Math.Abs(v - prev) > 1e-9)
                            updates[kv.Key] = updates.TryGetValue(kv.Key, out var uc) ? (uc + 1) : 1;
                        last[kv.Key] = v;
                    }
                    else
                    {
                        lastSet.Add(kv.Key);
                        last[kv.Key] = v;
                    }
                }
            }

            var result = new Dictionary<string, DiscoveryBehaviorSignature>(StringComparer.Ordinal);
            foreach (var kv in accum)
            {
                if (!byKey.TryGetValue(kv.Key, out var cand) || cand == null)
                    continue;

                var st = kv.Value;
                var count = nums.TryGetValue(kv.Key, out var n) ? n : 0;
                if (count <= 1)
                    continue;

                var upd = updates.TryGetValue(kv.Key, out var u) ? u : 0;
                var intCount = ints.TryGetValue(kv.Key, out var ic) ? ic : 0;
                var updateRate = (count <= 1) ? 0 : (upd / (double)(count - 1));

                var sig = new DiscoveryBehaviorSignature
                {
                    Fingerprint = cand.Fingerprint,
                    Kind = cand.Kind,
                    Min = st.Min,
                    Max = st.Max,
                    Mean = st.Mean,
                    Variance = st.Variance,
                    UpdateRate = updateRate,
                    MostlyInteger = count > 0 && (intCount / (double)count) >= 0.80,
                    UpdatedUtc = DateTime.UtcNow
                };
                if (!string.IsNullOrWhiteSpace(sig.Fingerprint))
                    result[sig.Fingerprint] = sig;
            }

            return result;
        }

        public Dictionary<string, string> InferMaxRelationships(DiscoveryScan scan, IReadOnlyList<DiscoveryObservationSample> samples, IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures, int perConcept = 70)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (samples == null || samples.Count == 0 || signatures == null || signatures.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var rel = new Dictionary<string, string>(StringComparer.Ordinal);

            InferMaxForPair(scan, samples, signatures, DiscoveryConcept.HealthCurrent, DiscoveryConcept.HealthMax, perConcept, rel);
            InferMaxForPair(scan, samples, signatures, DiscoveryConcept.AmmoCurrent, DiscoveryConcept.AmmoMax, perConcept, rel);
            InferMaxForPair(scan, samples, signatures, DiscoveryConcept.StaminaCurrent, DiscoveryConcept.StaminaMax, perConcept, rel);

            return rel;
        }

        public Dictionary<string, string> InferMaxRelationshipsFromSignatures(DiscoveryScan scan, IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures, int perConcept = 70)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (signatures == null || signatures.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var rel = new Dictionary<string, string>(StringComparer.Ordinal);

            InferMaxForPairFromSignatures(scan, signatures, DiscoveryConcept.HealthCurrent, DiscoveryConcept.HealthMax, perConcept, rel);
            InferMaxForPairFromSignatures(scan, signatures, DiscoveryConcept.AmmoCurrent, DiscoveryConcept.AmmoMax, perConcept, rel);
            InferMaxForPairFromSignatures(scan, signatures, DiscoveryConcept.StaminaCurrent, DiscoveryConcept.StaminaMax, perConcept, rel);

            return rel;
        }

        public List<DiscoveryRelationEdge> BuildRelationGraph(DiscoveryScan scan, IReadOnlyList<DiscoveryObservationSample> samples, IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures, IReadOnlyDictionary<string, string> currentToMaxKey, int maxNodes = 120, double minAbsCorr = 0.93)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (samples == null || samples.Count == 0)
                return new List<DiscoveryRelationEdge>();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (signatures != null)
            {
                foreach (var c in scan.Candidates)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Fingerprint))
                        continue;
                    if (signatures.ContainsKey(c.Fingerprint))
                        keys.Add(c.Key);
                    if (keys.Count >= maxNodes)
                        break;
                }
            }

            var keyList = keys.ToList();
            var edges = new List<DiscoveryRelationEdge>();

            for (var i = 0; i < keyList.Count; i++)
            {
                for (var j = i + 1; j < keyList.Count; j++)
                {
                    if (!TryComputePearson(samples, keyList[i], keyList[j], out var corr))
                        continue;
                    if (Math.Abs(corr) < minAbsCorr)
                        continue;

                    if (!scan.ByKey.TryGetValue(keyList[i], out var a) || !scan.ByKey.TryGetValue(keyList[j], out var b))
                        continue;
                    if (string.IsNullOrWhiteSpace(a.Fingerprint) || string.IsNullOrWhiteSpace(b.Fingerprint))
                        continue;

                    edges.Add(new DiscoveryRelationEdge
                    {
                        A_Fingerprint = a.Fingerprint,
                        B_Fingerprint = b.Fingerprint,
                        Kind = corr >= 0 ? "corr+" : "corr-",
                        Strength = corr,
                        UpdatedUtc = DateTime.UtcNow
                    });

                    if (edges.Count >= 1000)
                        break;
                }
                if (edges.Count >= 1000)
                    break;
            }

            if (currentToMaxKey != null)
            {
                foreach (var kv in currentToMaxKey)
                {
                    if (!scan.ByKey.TryGetValue(kv.Key, out var cur) || !scan.ByKey.TryGetValue(kv.Value, out var mx))
                        continue;
                    if (string.IsNullOrWhiteSpace(cur.Fingerprint) || string.IsNullOrWhiteSpace(mx.Fingerprint))
                        continue;
                    edges.Add(new DiscoveryRelationEdge
                    {
                        A_Fingerprint = cur.Fingerprint,
                        B_Fingerprint = mx.Fingerprint,
                        Kind = "max_of",
                        Strength = ComputeInequalityRate(samples, kv.Key, kv.Value),
                        UpdatedUtc = DateTime.UtcNow
                    });
                }
            }

            return edges;
        }

        private void InferMaxForPairFromSignatures(
            DiscoveryScan scan,
            IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures,
            DiscoveryConcept currentConcept,
            DiscoveryConcept maxConcept,
            int perConcept,
            Dictionary<string, string> currentToMax)
        {
            var currentRank = RankCandidates(scan, currentConcept, signatures, null, perConcept);
            var maxRank = RankCandidates(scan, maxConcept, signatures, null, perConcept);

            var maxCandidates = maxRank.Select(r => r.Candidate).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Key)).ToList();
            if (maxCandidates.Count == 0)
                return;

            for (var i = 0; i < currentRank.Count; i++)
            {
                var cur = currentRank[i]?.Candidate;
                if (cur == null || string.IsNullOrWhiteSpace(cur.Key) || string.IsNullOrWhiteSpace(cur.Fingerprint))
                    continue;
                if (!signatures.TryGetValue(cur.Fingerprint, out var curSig) || curSig?.Mean == null || curSig.Max == null)
                    continue;

                var bestKey = (string)null;
                var bestScore = 0d;

                for (var j = 0; j < maxCandidates.Count; j++)
                {
                    var mx = maxCandidates[j];
                    if (mx == null || mx.Key == cur.Key || string.IsNullOrWhiteSpace(mx.Fingerprint))
                        continue;
                    if (!signatures.TryGetValue(mx.Fingerprint, out var maxSig) || maxSig?.Mean == null || maxSig.Max == null)
                        continue;

                    if (maxSig.Mean.Value + 1e-6 < curSig.Mean.Value)
                        continue;

                    var stable = Clamp01(1.0 - (maxSig.UpdateRate / 0.08));
                    var covers = maxSig.Max.Value + 1e-6 >= curSig.Max.Value ? 1.0 : 0.0;
                    var sameType = string.Equals(cur.ComponentTypeName, mx.ComponentTypeName, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
                    var sameDecl = string.Equals(cur.DeclaringTypeName, mx.DeclaringTypeName, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

                    var tokens = Tokenize(mx.MemberName ?? "");
                    var nameMax = AnyTokenLike(tokens, new[] { "max", "maximum", "cap", "limit" }) ? 1.0 : 0.0;

                    var meanCloseness = 1.0 - Math.Abs(maxSig.Mean.Value - curSig.Mean.Value) / (Math.Abs(maxSig.Mean.Value) + 1.0);
                    meanCloseness = Clamp01(meanCloseness);

                    var score = (0.35 * stable) + (0.25 * covers) + (0.20 * nameMax) + (0.10 * meanCloseness) + (0.05 * sameType) + (0.05 * sameDecl);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestKey = mx.Key;
                    }
                }

                if (bestKey != null && bestScore >= 0.78)
                    currentToMax[cur.Key] = bestKey;
            }
        }

        private static bool TryComputePearson(IReadOnlyList<DiscoveryObservationSample> samples, string aKey, string bKey, out double corr)
        {
            corr = 0;
            var n = 0;
            var meanA = 0d;
            var meanB = 0d;
            var cAB = 0d;
            var m2A = 0d;
            var m2B = 0d;

            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s?.Values == null)
                    continue;
                if (!s.Values.TryGetValue(aKey, out var aText) || !s.Values.TryGetValue(bKey, out var bText))
                    continue;
                if (!double.TryParse(aText ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                    continue;
                if (!double.TryParse(bText ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                    continue;

                n++;
                var da = a - meanA;
                var db = b - meanB;
                meanA += da / n;
                meanB += db / n;
                cAB += da * (b - meanB);
                m2A += da * (a - meanA);
                m2B += db * (b - meanB);
            }

            if (n < 25)
                return false;
            if (m2A <= 1e-12 || m2B <= 1e-12)
                return false;
            corr = cAB / Math.Sqrt(m2A * m2B);
            if (double.IsNaN(corr) || double.IsInfinity(corr))
                return false;
            return true;
        }

        private void InferMaxForPair(
            DiscoveryScan scan,
            IReadOnlyList<DiscoveryObservationSample> samples,
            IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures,
            DiscoveryConcept currentConcept,
            DiscoveryConcept maxConcept,
            int perConcept,
            Dictionary<string, string> currentToMax)
        {
            var currentRank = RankCandidates(scan, currentConcept, signatures, null, perConcept);
            var maxRank = RankCandidates(scan, maxConcept, signatures, null, perConcept);

            var maxCandidates = maxRank.Select(r => r.Candidate).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Key)).ToList();
            if (maxCandidates.Count == 0)
                return;

            for (var i = 0; i < currentRank.Count; i++)
            {
                var cur = currentRank[i]?.Candidate;
                if (cur == null || string.IsNullOrWhiteSpace(cur.Key) || string.IsNullOrWhiteSpace(cur.Fingerprint))
                    continue;
                if (!signatures.TryGetValue(cur.Fingerprint, out var curSig) || curSig?.Max == null)
                    continue;

                var bestKey = (string)null;
                var bestScore = 0d;

                for (var j = 0; j < maxCandidates.Count; j++)
                {
                    var mx = maxCandidates[j];
                    if (mx == null || mx.Key == cur.Key || string.IsNullOrWhiteSpace(mx.Fingerprint))
                        continue;
                    if (!signatures.TryGetValue(mx.Fingerprint, out var maxSig) || maxSig?.Max == null)
                        continue;

                    var ineq = ComputeInequalityRate(samples, cur.Key, mx.Key);
                    if (ineq <= 0)
                        continue;

                    var stable = Clamp01(1.0 - (maxSig.UpdateRate / 0.06));
                    var rangeOk = (maxSig.Max.Value + 1e-6) >= (curSig.Max.Value - 1e-6) ? 1.0 : 0.0;
                    var score = (0.65 * ineq) + (0.20 * stable) + (0.15 * rangeOk);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestKey = mx.Key;
                    }
                }

                if (bestKey != null && bestScore >= 0.82)
                    currentToMax[cur.Key] = bestKey;
            }
        }

        private static double ComputeInequalityRate(IReadOnlyList<DiscoveryObservationSample> samples, string aKey, string bKey)
        {
            var compared = 0;
            var ok = 0;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s?.Values == null)
                    continue;
                if (!s.Values.TryGetValue(aKey, out var aText) || !s.Values.TryGetValue(bKey, out var bText))
                    continue;
                if (!double.TryParse(aText ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                    continue;
                if (!double.TryParse(bText ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                    continue;
                compared++;
                if (a <= b + 1e-6)
                    ok++;
            }
            if (compared < 20)
                return 0;
            return ok / (double)compared;
        }

        public IReadOnlyList<DiscoveryCandidateScore> ApplyExperimentEvidence(DiscoveryScan scan, DiscoveryConcept concept, IReadOnlyList<DiscoveryCandidateScore> baseRanking, DiscoveryExperimentEndResult deltas, int top = 250)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));
            if (baseRanking == null)
                throw new ArgumentNullException(nameof(baseRanking));
            if (deltas == null)
                return baseRanking.Take(top).ToList();

            var deltaByKey = deltas.Deltas.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Key)).ToDictionary(d => d.Key, d => d, StringComparer.Ordinal);
            var adjusted = new List<DiscoveryCandidateScore>(baseRanking.Count);

            for (var i = 0; i < baseRanking.Count; i++)
            {
                var r = baseRanking[i];
                if (r?.Candidate?.Key == null)
                    continue;
                var bonus = 0d;
                if (deltaByKey.TryGetValue(r.Candidate.Key, out var d))
                    bonus = ScoreDeltaEvidence(concept, d);

                adjusted.Add(new DiscoveryCandidateScore
                {
                    Candidate = r.Candidate,
                    Concept = r.Concept,
                    NameScore = r.NameScore,
                    TypeScore = r.TypeScore,
                    ContextScore = r.ContextScore,
                    DynamicScore = r.DynamicScore,
                    DatabaseScore = r.DatabaseScore,
                    KnowledgeScore = r.KnowledgeScore,
                    RelationScore = r.RelationScore,
                    CausalScore = r.CausalScore + bonus,
                    Score = r.Score + bonus
                });
            }

            return adjusted
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Candidate.MemberName, StringComparer.OrdinalIgnoreCase)
                .Take(top)
                .ToList();
        }

        private struct OnlineStats
        {
            public int Count;
            public double Mean;
            public double M2;
            public double Min;
            public double Max;

            public void Push(double x)
            {
                if (Count == 0)
                {
                    Count = 1;
                    Mean = x;
                    M2 = 0;
                    Min = x;
                    Max = x;
                    return;
                }

                Count++;
                if (x < Min) Min = x;
                if (x > Max) Max = x;

                var delta = x - Mean;
                Mean += delta / Count;
                var delta2 = x - Mean;
                M2 += delta * delta2;
            }

            public double Variance => Count > 1 ? (M2 / (Count - 1)) : 0;
        }

        public IReadOnlyList<DiscoveryCheatRecommendation> RecommendDynamicCheats(
            DiscoveryScan scan,
            IReadOnlyDictionary<string, DiscoveryBehaviorSignature> signatures,
            IReadOnlyDictionary<string, string> currentToMaxKey,
            DiscoveryKnowledgeBase kb,
            int topPerConcept = 3)
        {
            if (scan == null)
                throw new ArgumentNullException(nameof(scan));

            topPerConcept = Math.Max(1, Math.Min(10, topPerConcept));

            var concepts = new[]
            {
                DiscoveryConcept.HealthCurrent,
                DiscoveryConcept.HealthMax,
                DiscoveryConcept.ShieldCurrent,
                DiscoveryConcept.ShieldMax,
                DiscoveryConcept.AmmoCurrent,
                DiscoveryConcept.AmmoMax,
                DiscoveryConcept.StaminaCurrent,
                DiscoveryConcept.StaminaMax,
                DiscoveryConcept.Currency,
                DiscoveryConcept.Score,
                DiscoveryConcept.Cooldown,
                DiscoveryConcept.AbilityCharges,
                DiscoveryConcept.Lives,
                DiscoveryConcept.InventoryCount
            };

            var results = new List<DiscoveryCheatRecommendation>();

            for (var ci = 0; ci < concepts.Length; ci++)
            {
                var concept = concepts[ci];
                var ranking = RankCheatCandidates(scan, concept, signatures, currentToMaxKey, kb, 250);
                if (ranking == null || ranking.Count == 0)
                    continue;

                var emitted = 0;
                for (var i = 0; i < ranking.Count; i++)
                {
                    var r = ranking[i];
                    var cand = r?.Candidate;
                    if (cand == null || string.IsNullOrWhiteSpace(cand.Key))
                        continue;
                    if (!cand.CanWrite && concept != DiscoveryConcept.InventoryCount)
                        continue;
                    if (IsLikelyUiCandidate(cand))
                        continue;

                    var affinity = ScoreEntityAffinity(cand, concept);
                    if (affinity < 0.35 &&
                        concept != DiscoveryConcept.Currency &&
                        concept != DiscoveryConcept.Score &&
                        concept != DiscoveryConcept.InventoryCount)
                        continue;

                    if (concept == DiscoveryConcept.Cooldown && signatures != null && !string.IsNullOrWhiteSpace(cand.Fingerprint) && signatures.TryGetValue(cand.Fingerprint, out var sig))
                    {
                        if (sig != null && sig.Min != null && sig.Min.Value < -1e-3)
                            continue;
                    }

                    DiscoveryCandidate maxCandidate = null;
                    if (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.HealthCurrent)
                    {
                        if (currentToMaxKey != null && currentToMaxKey.TryGetValue(cand.Key, out var maxKey) && !string.IsNullOrWhiteSpace(maxKey))
                            scan.ByKey.TryGetValue(maxKey, out maxCandidate);
                    }

                    var mode = BuildCheatMode(concept);
                    var args = BuildCheatArgs(concept, cand, maxCandidate);

                    var stability = cand.TotalScans > 1 ? Clamp01(cand.PresenceRate) : 0;
                    var confidence = Clamp01((r.Score + (0.15 * affinity) + (0.10 * stability)) / 1.25);
                    var seen = cand.TotalScans > 1 ? $"{cand.SeenInScans}/{cand.TotalScans}" : "";
                    var reason = $"rank={r.Score.ToString("F3", CultureInfo.InvariantCulture)} affinity={affinity.ToString("F2", CultureInfo.InvariantCulture)} stable={stability.ToString("F2", CultureInfo.InvariantCulture)} seen={seen}";

                    results.Add(new DiscoveryCheatRecommendation
                    {
                        Concept = concept,
                        Category = GetCheatCategory(concept),
                        Candidate = cand,
                        Mode = mode,
                        Args = args,
                        Confidence = confidence,
                        Reason = reason
                    });

                    emitted++;
                    if (emitted >= topPerConcept)
                        break;
                }
            }

            return results
                .OrderByDescending(r => r.Confidence)
                .ThenBy(r => r.Concept)
                .Take(200)
                .ToList();
        }

        private static string GetCheatCategory(DiscoveryConcept concept)
        {
            return concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.HealthMax or DiscoveryConcept.StaminaCurrent or DiscoveryConcept.StaminaMax => "Player",
                DiscoveryConcept.ShieldCurrent or DiscoveryConcept.ShieldMax => "Player",
                DiscoveryConcept.Lives => "Player",
                DiscoveryConcept.AmmoCurrent or DiscoveryConcept.AmmoMax => "Weapon",
                DiscoveryConcept.AbilityCharges => "Abilities",
                DiscoveryConcept.Currency => "Economy",
                DiscoveryConcept.Cooldown => "Abilities",
                DiscoveryConcept.Score => "Economy",
                DiscoveryConcept.InventoryCount => "Inventory",
                _ => "General"
            };
        }

        private static bool IsLikelyUiCandidate(DiscoveryCandidate c)
        {
            var hay = (c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.GameObjectPath ?? "");
            var tokens = Tokenize(hay);
            if (tokens.Count == 0)
                return false;
            if (AnyTokenLike(tokens, new[] { "ui", "canvas", "hud", "text", "tmp", "button", "toggle", "slider", "dropdown", "image", "recttransform" }))
                return true;
            if (AnyTokenLike(tokens, new[] { "unityengine", "system", "il2cppsystem" }) && AnyTokenLike(tokens, new[] { "ui" }))
                return true;
            return false;
        }

        private static double ScoreEntityAffinity(DiscoveryCandidate c, DiscoveryConcept concept)
        {
            var hay = (c.GameObjectPath ?? "") + " " + (c.GameObjectName ?? "") + " " + (c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "");
            var tokens = Tokenize(hay);
            if (tokens.Count == 0)
                return 0;

            if (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.AmmoMax)
            {
                var pos = AnyTokenLike(tokens, new[] { "weapon", "gun", "rifle", "pistol", "shotgun", "bow", "projectile", "ammo" }) ? 1.0 : 0.0;
                var neg = AnyTokenLike(tokens, new[] { "ui", "hud", "canvas", "menu" }) ? 1.0 : 0.0;
                return Clamp01(pos - (0.6 * neg));
            }

            if (concept == DiscoveryConcept.HealthCurrent ||
                concept == DiscoveryConcept.HealthMax ||
                concept == DiscoveryConcept.StaminaCurrent ||
                concept == DiscoveryConcept.StaminaMax ||
                concept == DiscoveryConcept.ShieldCurrent ||
                concept == DiscoveryConcept.ShieldMax ||
                concept == DiscoveryConcept.Lives)
            {
                var pos = AnyTokenLike(tokens, new[] { "player", "localplayer", "character", "pawn", "hero", "avatar", "controller" }) ? 1.0 : 0.0;
                var neg = AnyTokenLike(tokens, new[] { "enemy", "npc", "monster", "ai" }) ? 0.7 : 0.0;
                return Clamp01(pos - (0.5 * neg));
            }

            if (concept == DiscoveryConcept.Cooldown || concept == DiscoveryConcept.AbilityCharges)
            {
                var pos = AnyTokenLike(tokens, new[] { "ability", "skill", "spell", "cooldown", "cd", "charge" }) ? 1.0 : 0.0;
                var neg = AnyTokenLike(tokens, new[] { "ui", "hud", "canvas", "menu" }) ? 1.0 : 0.0;
                return Clamp01(pos - (0.6 * neg));
            }

            return 0.5;
        }

        public string BuildCheatMode(DiscoveryConcept concept)
        {
            return concept switch
            {
                DiscoveryConcept.HealthCurrent => "nodecrease",
                DiscoveryConcept.AmmoCurrent => "autofill",
                DiscoveryConcept.StaminaCurrent => "autofill",
                DiscoveryConcept.Currency => "nodecrease",
                DiscoveryConcept.Cooldown => "clampmax",
                DiscoveryConcept.InventoryCount => "clampmin",
                DiscoveryConcept.ShieldCurrent => "nodecrease",
                DiscoveryConcept.Lives => "nodecrease",
                _ => "freeze"
            };
        }

        public IReadOnlyDictionary<string, string> BuildCheatArgs(DiscoveryConcept concept, DiscoveryCandidate candidate, DiscoveryCandidate maxCandidate = null)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.HealthCurrent)
            {
                if (maxCandidate != null && !string.IsNullOrWhiteSpace(maxCandidate.Key))
                {
                    args["maxKey"] = maxCandidate.Key;
                    args["threshold"] = "0.95";
                }
            }

            if (concept == DiscoveryConcept.Cooldown)
            {
                args["value"] = "0";
            }

            return args;
        }

        private static DiscoveryCandidateScore ScoreCandidate(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryBehaviorSignature sig, IReadOnlyDictionary<string, string> currentToMaxKey, DiscoveryKnowledgeBase kb)
        {
            var nameScore = ScoreName(c, concept);
            var typeScore = ScoreType(c, concept, sig);
            var ctxScore = ScoreContext(c, concept);
            var dynScore = ScoreDynamic(c, concept, sig);
            var dbScore = ScoreDatabase(c, concept);
            var knowScore = ScoreKnowledge(c, concept, kb);
            var causalPrior = ScoreCausalPrior(c, concept, kb);
            var stability = ScoreStability(c);
            var affinity = ScoreEntityAffinity(c, concept);
            var hasWriteEvidence = TryGetWriteProbeEvidence(c, concept, kb, out var writeProbe);
            var writeReliability = hasWriteEvidence ? ScoreWriteProbeReliability(writeProbe) : 0d;

            double wName, wType, wCtx, wDyn, wDb, wKnow, wCausal;
            switch (concept)
            {
                case DiscoveryConcept.HealthCurrent:
                case DiscoveryConcept.StaminaCurrent:
                case DiscoveryConcept.ShieldCurrent:
                    wName = 0.40; wDyn = 0.48; wDb = 0.28; wCtx = 0.25; wKnow = 0.25; wType = 0.18; wCausal = 0.22;
                    break;
                case DiscoveryConcept.AmmoCurrent:
                case DiscoveryConcept.AbilityCharges:
                    wName = 0.45; wDyn = 0.38; wDb = 0.30; wCtx = 0.22; wKnow = 0.25; wType = 0.25; wCausal = 0.18;
                    break;
                case DiscoveryConcept.Currency:
                case DiscoveryConcept.Score:
                case DiscoveryConcept.Lives:
                    wName = 0.36; wDyn = 0.18; wDb = 0.34; wCtx = 0.26; wKnow = 0.25; wType = 0.36; wCausal = 0.12;
                    break;
                case DiscoveryConcept.Cooldown:
                    wName = 0.36; wDyn = 0.52; wDb = 0.28; wCtx = 0.26; wKnow = 0.20; wType = 0.26; wCausal = 0.22;
                    break;
                case DiscoveryConcept.InventoryCount:
                    wName = 0.34; wDyn = 0.22; wDb = 0.26; wCtx = 0.26; wKnow = 0.18; wType = 0.42; wCausal = 0.12;
                    break;
                case DiscoveryConcept.HealthMax:
                case DiscoveryConcept.AmmoMax:
                case DiscoveryConcept.StaminaMax:
                case DiscoveryConcept.ShieldMax:
                    wName = 0.42; wDyn = 0.40; wDb = 0.26; wCtx = 0.24; wKnow = 0.22; wType = 0.24; wCausal = 0.18;
                    break;
                default:
                    wName = 0.38; wDyn = 0.32; wDb = 0.26; wCtx = 0.24; wKnow = 0.20; wType = 0.20; wCausal = 0.18;
                    break;
            }

            var combined = 1.0;
            combined *= 1.0 - (wName * Clamp01(nameScore));
            combined *= 1.0 - (wDb * Clamp01(dbScore));
            combined *= 1.0 - (wCtx * Clamp01(ctxScore));
            combined *= 1.0 - (wDyn * Clamp01(dynScore));
            combined *= 1.0 - (wKnow * Clamp01(knowScore));
            combined *= 1.0 - (wType * Clamp01(typeScore));
            combined *= 1.0 - (wCausal * Clamp01(causalPrior));

            var score = Clamp01(1.0 - combined);

            var relScore = 0d;
            if (currentToMaxKey != null && !string.IsNullOrWhiteSpace(c.Key))
            {
                if (concept == DiscoveryConcept.HealthCurrent || concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.ShieldCurrent)
                {
                    if (currentToMaxKey.ContainsKey(c.Key))
                        relScore = 1.0;
                }
                else if (concept == DiscoveryConcept.HealthMax || concept == DiscoveryConcept.AmmoMax || concept == DiscoveryConcept.StaminaMax || concept == DiscoveryConcept.ShieldMax)
                {
                    if (currentToMaxKey.Values.Contains(c.Key))
                        relScore = 0.8;
                }
            }
            score = Clamp01(score + (0.10 * Clamp01(relScore)) + (0.12 * Clamp01(affinity)) + (0.10 * Clamp01(stability)));

            if (!c.CanWrite && concept != DiscoveryConcept.InventoryCount)
                score *= 0.55;
            if (string.IsNullOrWhiteSpace(c.MemberName))
                score *= 0.40;
            if (c.IsStatic)
                score *= 0.78;

            score *= ScoreNoisePenalty(c, concept);

            if (hasWriteEvidence)
            {
                score = Clamp01(score + ((writeReliability - 0.5) * 0.14));
                if (writeReliability < 0.25)
                    score *= 0.80;
            }

            return new DiscoveryCandidateScore
            {
                Candidate = c,
                Concept = concept,
                Score = score,
                NameScore = nameScore,
                TypeScore = typeScore,
                ContextScore = ctxScore,
                DynamicScore = dynScore,
                DatabaseScore = dbScore,
                KnowledgeScore = knowScore,
                RelationScore = relScore,
                CausalScore = causalPrior
            };
        }

        private static bool TryGetWriteProbeEvidence(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryKnowledgeBase kb, out DiscoveryKnowledgeWriteProbe probe)
        {
            probe = null;
            if (kb?.WriteProbes == null || kb.WriteProbes.Count == 0 || c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                return false;

            var now = DateTime.UtcNow;
            DiscoveryKnowledgeWriteProbe best = null;
            for (var i = 0; i < kb.WriteProbes.Count; i++)
            {
                var p = kb.WriteProbes[i];
                if (p == null)
                    continue;
                if (p.Concept != concept)
                    continue;
                if (string.IsNullOrWhiteSpace(p.Fingerprint) || !string.Equals(p.Fingerprint, c.Fingerprint, StringComparison.Ordinal))
                    continue;
                if ((now - p.UpdatedUtc).TotalDays > 180)
                    continue;
                if (best == null || p.UpdatedUtc > best.UpdatedUtc)
                    best = p;
            }

            if (best == null)
                return false;
            probe = best;
            return true;
        }

        private static double ScoreWriteProbeReliability(DiscoveryKnowledgeWriteProbe probe)
        {
            if (probe == null)
                return 0;
            if (!probe.Wrote)
                return 0;
            if (probe.Sticky)
                return 1.0;
            if (probe.RubberBand || probe.ClampDetected)
                return 0.0;
            return Clamp01(probe.Score);
        }

        private static double ScoreStability(DiscoveryCandidate c)
        {
            if (c == null)
                return 0;
            if (c.TotalScans <= 1)
                return 0;
            if (c.PresenceRate > 0)
                return Clamp01((Clamp01(c.PresenceRate) - 0.25) / 0.75);
            if (c.SeenInScans > 0 && c.TotalScans > 0)
                return Clamp01(((c.SeenInScans / (double)c.TotalScans) - 0.25) / 0.75);
            return 0;
        }

        private static double ScoreKnowledge(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryKnowledgeBase kb)
        {
            if (kb == null || c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                return 0;

            var confirmed = 0d;
            if (kb.ConfirmedMappings != null)
            {
                for (var i = 0; i < kb.ConfirmedMappings.Count; i++)
                {
                    var m = kb.ConfirmedMappings[i];
                    if (m.Concept == concept && string.Equals(m.Fingerprint, c.Fingerprint, StringComparison.Ordinal))
                    {
                        confirmed = 1.0;
                        break;
                    }
                }
            }

            var learned = ScoreLearnedTokens(c, concept, kb);
            var feedback = ScoreFeedback(c, concept, kb);
            return Math.Max(confirmed, Clamp01(learned + feedback));
        }

        private static double ScoreCausalPrior(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryKnowledgeBase kb)
        {
            if (kb?.CausalPriors == null || kb.CausalPriors.Count == 0 || c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                return 0;

            var best = 0d;
            var now = DateTime.UtcNow;
            for (var i = 0; i < kb.CausalPriors.Count; i++)
            {
                var p = kb.CausalPriors[i];
                if (p == null)
                    continue;
                if (p.Concept != concept)
                    continue;
                if (!string.Equals(p.Fingerprint, c.Fingerprint, StringComparison.Ordinal))
                    continue;

                var ageDays = (now - p.UpdatedUtc).TotalDays;
                if (ageDays > 120)
                    continue;

                var decay = 1.0 - Math.Min(1.0, ageDays / 120.0);
                var s = Clamp01(p.Strength) * decay;
                if (s > best)
                    best = s;
            }

            return best;
        }

        private static double ScoreLearnedTokens(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryKnowledgeBase kb)
        {
            if (kb?.TokenWeights == null || kb.TokenWeights.Count == 0 || c == null)
                return 0;

            var hay = (c.MemberName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.ComponentTypeName ?? "") + " " + (c.GameObjectName ?? "") + " " + (c.GameObjectPath ?? "");
            var tokens = Tokenize(hay);
            if (tokens.Count == 0)
                return 0;

            var now = DateTime.UtcNow;
            var sum = 0d;
            for (var i = 0; i < kb.TokenWeights.Count; i++)
            {
                var tw = kb.TokenWeights[i];
                if (tw == null || tw.Concept != concept || string.IsNullOrWhiteSpace(tw.Token))
                    continue;

                var ageDays = (now - tw.UpdatedUtc).TotalDays;
                if (ageDays > 240)
                    continue;

                if (!ContainsTokenLike(tokens, tw.Token))
                    continue;

                var decay = 1.0 - Math.Min(1.0, ageDays / 240.0);
                sum += Clamp01((tw.Weight + 1.0) / 2.0) * decay;
                if (sum >= 2.0)
                    break;
            }

            return Clamp01(sum / 1.2);
        }

        private static double ScoreFeedback(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryKnowledgeBase kb)
        {
            if (kb?.Feedback == null || kb.Feedback.Count == 0 || c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                return 0;

            var now = DateTime.UtcNow;
            var sum = 0d;
            for (var i = 0; i < kb.Feedback.Count; i++)
            {
                var f = kb.Feedback[i];
                if (f == null || f.Concept != concept || string.IsNullOrWhiteSpace(f.Fingerprint))
                    continue;
                if (!string.Equals(f.Fingerprint, c.Fingerprint, StringComparison.Ordinal))
                    continue;

                var ageDays = (now - f.UpdatedUtc).TotalDays;
                if (ageDays > 180)
                    continue;

                var decay = 1.0 - Math.Min(1.0, ageDays / 180.0);
                var kind = (f.Kind ?? "").Trim();
                var sgn = kind.Equals("down", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
                sum += sgn * Clamp01(f.Strength) * decay;
                if (Math.Abs(sum) >= 1.0)
                    break;
            }

            return Math.Max(-0.25, Math.Min(0.25, sum * 0.25));
        }

        private static double ScoreDynamic(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryBehaviorSignature sig)
        {
            if (sig == null || sig.Min == null || sig.Max == null || sig.Mean == null || sig.Variance == null)
                return 0;

            var min = sig.Min.Value;
            var max = sig.Max.Value;
            var mean = sig.Mean.Value;
            var range = max - min;
            var update = sig.UpdateRate;

            var nonNeg = min >= -1e-3 ? 1.0 : 0.3;
            var integerish = sig.MostlyInteger ? 1.0 : 0.45;
            var rangeSmall = range <= 1e-3 ? 1.0 : Clamp01(1.0 / (1.0 + range));
            var rangeLarge = Clamp01(range / 50.0);

            if (concept == DiscoveryConcept.HealthCurrent || concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.ShieldCurrent)
            {
                var upd = 1.0 - Math.Abs(update - 0.08) / 0.25;
                return Clamp01((0.35 * nonNeg) + (0.25 * integerish) + (0.20 * Clamp01(upd)) + (0.20 * rangeLarge));
            }

            if (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.AbilityCharges)
            {
                var upd = 1.0 - Math.Abs(update - 0.03) / 0.18;
                var smallRange = Clamp01(1.0 - (range / 200.0));
                return Clamp01((0.35 * integerish) + (0.25 * nonNeg) + (0.25 * Clamp01(upd)) + (0.15 * smallRange));
            }

            if (concept == DiscoveryConcept.HealthMax || concept == DiscoveryConcept.AmmoMax || concept == DiscoveryConcept.StaminaMax || concept == DiscoveryConcept.ShieldMax)
            {
                var stable = Clamp01(1.0 - (update / 0.05));
                var fixedish = Clamp01(1.0 - (range / (Math.Abs(mean) + 1.0)));
                return Clamp01((0.45 * stable) + (0.25 * nonNeg) + (0.15 * fixedish) + (0.15 * integerish));
            }

            if (concept == DiscoveryConcept.Currency || concept == DiscoveryConcept.Score || concept == DiscoveryConcept.Lives)
            {
                var stable = Clamp01(1.0 - (update / 0.04));
                var magnitude = Clamp01(Math.Log10(Math.Abs(max) + 1.0) / 6.0);
                return Clamp01((0.40 * stable) + (0.30 * integerish) + (0.20 * nonNeg) + (0.10 * magnitude));
            }

            if (concept == DiscoveryConcept.Cooldown)
            {
                var minNearZero = Clamp01(1.0 - Math.Min(1.0, Math.Abs(min) / 0.2));
                var active = Clamp01(update / 0.10);
                var nonNegScore = nonNeg;
                return Clamp01((0.40 * minNearZero) + (0.35 * active) + (0.15 * nonNegScore) + (0.10 * rangeLarge));
            }

            if (concept == DiscoveryConcept.InventoryCount)
            {
                var stable = Clamp01(1.0 - (update / 0.03));
                return Clamp01((0.45 * integerish) + (0.35 * stable) + (0.20 * nonNeg));
            }

            if (c.Kind == DiscoveryCandidateKind.Numeric)
            {
                var stable = Clamp01(1.0 - (update / 0.10));
                return Clamp01((0.5 * stable) + (0.5 * nonNeg));
            }

            return 0;
        }

        private static double ScoreType(DiscoveryCandidate c, DiscoveryConcept concept, DiscoveryBehaviorSignature sig)
        {
            if (c == null)
                return 0;

            var typeTokens = GetCandidateTypeTokens(c);
            var isIntLike = typeTokens.Contains("int") || typeTokens.Contains("long");
            var isFloatLike = typeTokens.Contains("float") || typeTokens.Contains("double");
            var isBool = typeTokens.Contains("bool");
            var isCollection = c.Kind == DiscoveryCandidateKind.CollectionCount || typeTokens.Contains("list") || typeTokens.Contains("array") || typeTokens.Contains("dictionary");

            if (isBool)
                return 0;

            if (concept == DiscoveryConcept.InventoryCount)
            {
                if (c.Kind == DiscoveryCandidateKind.CollectionCount)
                    return 1.0;
                if (isIntLike)
                    return 0.85;
                if (c.Kind == DiscoveryCandidateKind.Numeric)
                    return 0.65;
                return isCollection ? 0.55 : 0;
            }

            if (concept == DiscoveryConcept.Currency || concept == DiscoveryConcept.Score || concept == DiscoveryConcept.Lives || concept == DiscoveryConcept.AbilityCharges)
            {
                if (sig != null)
                    return sig.MostlyInteger ? 1.0 : 0.25;
                if (isIntLike)
                    return 1.0;
                if (isFloatLike)
                    return 0.35;
                return c.Kind == DiscoveryCandidateKind.Numeric ? 0.55 : 0;
            }

            if (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.AmmoMax)
            {
                if (sig != null)
                    return sig.MostlyInteger ? 1.0 : 0.25;
                return isIntLike ? 1.0 : (c.Kind == DiscoveryCandidateKind.Numeric ? 0.45 : 0);
            }

            if (concept == DiscoveryConcept.HealthCurrent ||
                concept == DiscoveryConcept.HealthMax ||
                concept == DiscoveryConcept.StaminaCurrent ||
                concept == DiscoveryConcept.StaminaMax ||
                concept == DiscoveryConcept.ShieldCurrent ||
                concept == DiscoveryConcept.ShieldMax)
            {
                if (sig != null)
                    return sig.MostlyInteger ? 0.75 : 0.90;
                if (isFloatLike)
                    return 0.90;
                if (isIntLike)
                    return 0.80;
                return c.Kind == DiscoveryCandidateKind.Numeric ? 0.60 : 0;
            }

            if (concept == DiscoveryConcept.Cooldown)
            {
                if (sig != null)
                    return sig.MostlyInteger ? 0.70 : 0.95;
                if (isFloatLike)
                    return 0.95;
                if (isIntLike)
                    return 0.70;
                return c.Kind == DiscoveryCandidateKind.Numeric ? 0.55 : 0;
            }

            if (c.Kind == DiscoveryCandidateKind.Numeric)
                return 1.0;
            if (isCollection)
                return 0.30;
            return 0;
        }

        private static double ScoreNoisePenalty(DiscoveryCandidate c, DiscoveryConcept concept)
        {
            if (c == null)
                return 1.0;

            var penalty = 1.0;
            var typeHay = ((c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "")).Trim();
            var pathHay = (c.GameObjectPath ?? "").Trim();

            if (StartsWithAny(typeHay, "UnityEngine.", "TMPro.", "UnityEngine.UI", "Il2CppSystem.", "System."))
                penalty *= 0.20;

            var uiTokens = Tokenize(typeHay + " " + pathHay + " " + (c.GameObjectName ?? ""));
            if (AnyTokenLike(uiTokens, new[] { "ui", "hud", "canvas", "menu", "panel", "overlay", "text", "label", "image", "icon", "sprite", "slider", "scroll", "button", "toggle", "dropdown", "recttransform" }))
            {
                if (concept == DiscoveryConcept.InventoryCount)
                    penalty *= 0.65;
                else
                    penalty *= 0.40;
            }

            if (AnyTokenLike(uiTokens, new[] { "debug", "test", "demo", "example", "editor" }))
                penalty *= 0.60;

            if (c.IsStatic)
                penalty *= 0.70;

            return Clamp01(penalty);
        }

        private static bool StartsWithAny(string text, params string[] prefixes)
        {
            if (string.IsNullOrWhiteSpace(text) || prefixes == null || prefixes.Length == 0)
                return false;
            for (var i = 0; i < prefixes.Length; i++)
            {
                var p = prefixes[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static double ScoreContext(DiscoveryCandidate c, DiscoveryConcept concept)
        {
            var hay = (c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.GameObjectName ?? "") + " " + (c.GameObjectPath ?? "");
            var tokens = Tokenize(hay);

            string[] positive =
                concept == DiscoveryConcept.HealthCurrent || concept == DiscoveryConcept.HealthMax || concept == DiscoveryConcept.ShieldCurrent || concept == DiscoveryConcept.ShieldMax ? new[] { "player", "localplayer", "character", "pawn", "hero", "avatar", "controller", "health", "hp", "life", "hitpoint", "vital", "vitality", "damage", "hurt", "shield" } :
                concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.AmmoMax ? new[] { "weapon", "gun", "rifle", "pistol", "shotgun", "bow", "projectile", "ammo", "bullet", "mag", "magazine", "clip", "round", "shell" } :
                concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.StaminaMax ? new[] { "player", "character", "stamina", "sprint", "run", "energy", "fatigue", "endurance", "breath" } :
                concept == DiscoveryConcept.Currency || concept == DiscoveryConcept.Score ? new[] { "money", "currency", "wallet", "shop", "store", "credit", "gold", "coin", "cash", "balance", "funds", "score", "points", "xp" } :
                concept == DiscoveryConcept.Cooldown || concept == DiscoveryConcept.AbilityCharges ? new[] { "cooldown", "cooltime", "cd", "timer", "ability", "skill", "spell", "charge", "charges", "recharge", "cast", "delay" } :
                concept == DiscoveryConcept.Lives ? new[] { "life", "lives", "respawn", "continue", "tries" } :
                concept == DiscoveryConcept.InventoryCount ? new[] { "inventory", "item", "items", "slot", "slots", "bag", "backpack", "pickup", "loot", "stack", "capacity" } :
                Array.Empty<string>();

            var hit = 0;
            for (var i = 0; i < positive.Length; i++)
                if (ContainsTokenLike(tokens, positive[i]))
                    hit++;

            return Clamp01(hit / 2.0);
        }

        private static double ScoreName(DiscoveryCandidate c, DiscoveryConcept concept)
        {
            var tokens = Tokenize((c.MemberName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.ComponentTypeName ?? "") + " " + (c.GameObjectPath ?? ""));

            var primary = concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.HealthMax => new[] { "health", "hp", "life", "hitpoint", "hitpoints", "vital", "vitality", "hearts", "hull", "shield" },
                DiscoveryConcept.ShieldCurrent or DiscoveryConcept.ShieldMax => new[] { "shield", "barrier", "armor", "armour", "ward" },
                DiscoveryConcept.AmmoCurrent or DiscoveryConcept.AmmoMax => new[] { "ammo", "ammunition", "bullet", "clip", "mag", "magazine", "round", "shell", "cartridge", "charges", "charge" },
                DiscoveryConcept.StaminaCurrent or DiscoveryConcept.StaminaMax => new[] { "stamina", "sprint", "energy", "fatigue", "endurance", "breath" },
                DiscoveryConcept.Currency or DiscoveryConcept.Score => new[] { "money", "currency", "gold", "coin", "cash", "credit", "credits", "wallet", "balance", "funds", "scrap", "souls", "tokens", "score", "points", "xp" },
                DiscoveryConcept.Cooldown => new[] { "cooldown", "cooltime", "cd", "timer", "delay", "remain", "remaining", "recharge", "charge", "charges" },
                DiscoveryConcept.AbilityCharges => new[] { "charges", "charge", "ammo", "stack", "stacks", "uses", "usecount" },
                DiscoveryConcept.Lives => new[] { "lives", "life", "respawn", "continues", "continue", "tries" },
                DiscoveryConcept.InventoryCount => new[] { "inventory", "item", "items", "slot", "slots", "count", "capacity", "stack", "quantity", "amount" },
                _ => Array.Empty<string>()
            };

            var currentQual = new[] { "current", "cur", "now", "value", "amount", "left", "remain", "remaining" };
            var maxQual = new[] { "max", "maximum", "cap", "limit", "full", "total", "capacity", "size" };

            var hits = 0;
            for (var i = 0; i < primary.Length; i++)
                if (ContainsTokenLike(tokens, primary[i]))
                    hits++;

            var p = Clamp01(hits / 2.0);

            if (p <= 0)
                return 0;

            var q = 0d;
            var penal = 0d;

            if (concept == DiscoveryConcept.HealthCurrent || concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.StaminaCurrent)
            {
                q = AnyTokenLike(tokens, currentQual) ? 0.55 : 0.15;
                penal = AnyTokenLike(tokens, maxQual) ? 0.35 : 0.0;
            }
            else if (concept == DiscoveryConcept.HealthMax || concept == DiscoveryConcept.AmmoMax || concept == DiscoveryConcept.StaminaMax)
            {
                q = AnyTokenLike(tokens, maxQual) ? 0.55 : 0.15;
                penal = AnyTokenLike(tokens, currentQual) ? 0.25 : 0.0;
            }
            else if (concept == DiscoveryConcept.Cooldown)
            {
                q = AnyTokenLike(tokens, new[] { "remaining", "remain", "cooldown", "cd", "timer" }) ? 0.5 : 0.1;
            }
            else if (concept == DiscoveryConcept.Currency)
            {
                q = AnyTokenLike(tokens, new[] { "total", "amount", "balance", "wallet" }) ? 0.25 : 0.1;
            }
            else if (concept == DiscoveryConcept.InventoryCount)
            {
                q = AnyTokenLike(tokens, new[] { "count", "num", "size", "capacity", "slots" }) ? 0.45 : 0.1;
            }

            var score = Math.Max(0, p + q - penal);
            return Math.Min(1.0, score);
        }

        private static double ScoreDatabase(DiscoveryCandidate c, DiscoveryConcept concept)
        {
            try
            {
                if (c == null)
                    return 0;

                var db = DiscoveryTextDatabaseProvider.Get();
                if (db == null || db.Sections == null || db.Sections.Count == 0)
                    return 0;

                var sectionName = GetDatabaseSectionName(concept);
                if (string.IsNullOrWhiteSpace(sectionName))
                    return 0;
                if (!db.TryGetSection(sectionName, out var section) || section == null)
                    return 0;

                var nameTokens = Tokenize((c.MemberName ?? "") + " " + (c.DeclaringTypeName ?? ""));
                var compTokens = Tokenize((c.ComponentTypeName ?? "") + " " + (c.DeclaringTypeName ?? "") + " " + (c.GameObjectPath ?? ""));

                var fieldHits = CountDatabaseTokenHits(nameTokens, section.FieldNameTokens, 2);

                var compHits = CountDatabaseTokenHits(compTokens, section.ComponentTokens, 3);

                var typeHit = false;
                if (section.TypeTokens != null && section.TypeTokens.Count > 0)
                {
                    var candType = GetCandidateTypeTokens(c);
                    foreach (var t in candType)
                    {
                        if (section.TypeTokens.Contains(t))
                        {
                            typeHit = true;
                            break;
                        }
                    }
                }

                var score = 0d;
                if (fieldHits > 0)
                    score += fieldHits >= 2 ? 0.65 : 0.55;
                if (compHits > 0)
                    score += Math.Min(0.25, 0.08 * compHits);
                if (typeHit)
                    score += 0.20;

                return Clamp01(score);
            }
            catch
            {
                return 0;
            }
        }

        private static int CountDatabaseTokenHits(HashSet<string> candidateTokens, HashSet<string> databaseTokens, int maxHits)
        {
            if (maxHits <= 0)
                return 0;
            if (candidateTokens == null || candidateTokens.Count == 0 || databaseTokens == null || databaseTokens.Count == 0)
                return 0;

            var hits = 0;
            foreach (var t in candidateTokens)
            {
                if (t == null)
                    continue;
                if (databaseTokens.Contains(t))
                {
                    hits++;
                    if (hits >= maxHits)
                        break;
                }
            }
            return hits;
        }

        private static string GetDatabaseSectionName(DiscoveryConcept concept)
        {
            return concept switch
            {
                DiscoveryConcept.HealthCurrent => "HEALTH",
                DiscoveryConcept.HealthMax => "HEALTH",
                DiscoveryConcept.AmmoCurrent => "AMMO",
                DiscoveryConcept.AmmoMax => "AMMO",
                DiscoveryConcept.StaminaCurrent => "STAMINA",
                DiscoveryConcept.StaminaMax => "STAMINA",
                DiscoveryConcept.Currency => "CURRENCY",
                DiscoveryConcept.Cooldown => "COOLDOWN",
                DiscoveryConcept.InventoryCount => "INVENTORY",
                _ => concept.ToString().ToUpperInvariant()
            };
        }

        private static HashSet<string> GetCandidateTypeTokens(DiscoveryCandidate c)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (c == null)
                return set;

            var t = (c.MemberTypeName ?? "").Trim();
            var tl = t.ToLowerInvariant();
            if (tl.Length == 0)
                return set;

            if (c.Kind == DiscoveryCandidateKind.CollectionCount)
            {
                set.Add("list");
                set.Add("array");
                set.Add("dictionary");
            }

            if (tl == "float" || tl.EndsWith(".single", StringComparison.Ordinal) || tl.EndsWith("+single", StringComparison.Ordinal))
                set.Add("float");
            if (tl == "double" || tl.EndsWith(".double", StringComparison.Ordinal))
                set.Add("double");

            if (tl == "int" || tl == "int32" || tl.EndsWith(".int32", StringComparison.Ordinal))
                set.Add("int");
            if (tl == "long" || tl == "int64" || tl.EndsWith(".int64", StringComparison.Ordinal))
                set.Add("long");

            if (tl == "bool" || tl == "boolean" || tl.EndsWith(".boolean", StringComparison.Ordinal))
                set.Add("bool");

            if (tl.Contains("list") || tl.Contains("system.collections.generic.list"))
                set.Add("list");
            if (tl.Contains("dictionary") || tl.Contains("system.collections.generic.dictionary"))
                set.Add("dictionary");
            if (tl.EndsWith("[]", StringComparison.Ordinal) || tl.Contains("system.array"))
                set.Add("array");

            if (set.Count == 0 && c.Kind == DiscoveryCandidateKind.Numeric)
                set.Add("float");
            return set;
        }

        private static double ScoreDeltaEvidence(DiscoveryConcept concept, DiscoveryExperimentDelta d)
        {
            if (d == null)
                return 0;

            if (concept == DiscoveryConcept.HealthCurrent)
            {
                if (d.Delta.HasValue && d.Delta.Value < 0)
                    return Clamp01(Math.Min(0.8, Math.Abs(d.Delta.Value) / 25.0) + 0.25);
                if (d.Delta.HasValue && d.Delta.Value > 0)
                    return 0.05;
                return 0;
            }

            if (concept == DiscoveryConcept.AmmoCurrent)
            {
                if (d.Delta.HasValue && d.Delta.Value < 0)
                    return Clamp01(Math.Min(0.7, Math.Abs(d.Delta.Value) / 5.0) + 0.2);
                return 0;
            }

            if (concept == DiscoveryConcept.StaminaCurrent)
            {
                if (d.Delta.HasValue && d.Delta.Value < 0)
                    return Clamp01(Math.Min(0.6, Math.Abs(d.Delta.Value) / 20.0) + 0.2);
                return 0;
            }

            if (concept == DiscoveryConcept.Currency)
            {
                if (d.Delta.HasValue && Math.Abs(d.Delta.Value) >= 1.0)
                    return 0.25;
                return 0;
            }

            if (concept == DiscoveryConcept.Cooldown)
            {
                if (d.Delta.HasValue && d.Delta.Value > 0)
                    return 0.25;
                return 0;
            }

            if (concept == DiscoveryConcept.InventoryCount)
            {
                if (d.Delta.HasValue && d.Delta.Value > 0)
                    return 0.25;
                return 0;
            }

            return 0;
        }

        private static bool AnyTokenLike(HashSet<string> tokens, string[] patterns)
        {
            for (var i = 0; i < patterns.Length; i++)
                if (ContainsTokenLike(tokens, patterns[i]))
                    return true;
            return false;
        }

        private static bool ContainsTokenLike(HashSet<string> tokens, string pattern)
        {
            if (tokens == null || tokens.Count == 0 || string.IsNullOrWhiteSpace(pattern))
                return false;
            var p = pattern.ToLowerInvariant();
            foreach (var t in tokens)
            {
                if (t == p)
                    return true;
                if (p.Length >= 4 && t.StartsWith(p, StringComparison.Ordinal))
                    return true;
                if (t.Length >= 5 && p.StartsWith(t, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static HashSet<string> Tokenize(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text))
                return set;

            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (sb.Length > 0 && char.IsUpper(c) && char.IsLower(sb[sb.Length - 1]))
                    {
                        AddToken(set, sb);
                        sb.Clear();
                    }
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    AddToken(set, sb);
                    sb.Clear();
                }
            }
            AddToken(set, sb);
            return set;
        }

        private static void AddToken(HashSet<string> set, StringBuilder sb)
        {
            if (sb == null || sb.Length == 0)
                return;
            var s = sb.ToString().Trim();
            if (s.Length <= 1)
                return;
            set.Add(s);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }

    public static class DiscoveryKnowledgeBaseStore
    {
        public static string GetGameId(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return "unknown";
            var norm = gameDirectory.Trim().ToLowerInvariant();
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
            return Convert.ToHexString(hash).Substring(0, 12);
        }

        public static string GetKnowledgeBasePath(string gameDirectory)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhantomLink", "discovery");
            Directory.CreateDirectory(root);
            var id = GetGameId(gameDirectory);
            return Path.Combine(root, $"{id}.json");
        }

        public static DiscoveryKnowledgeBase Load(string gameDirectory)
        {
            try
            {
                var path = GetKnowledgeBasePath(gameDirectory);
                if (!File.Exists(path))
                    return new DiscoveryKnowledgeBase { GameDirectory = gameDirectory, GameId = GetGameId(gameDirectory) };
                var json = File.ReadAllText(path);
                var kb = JsonConvert.DeserializeObject<DiscoveryKnowledgeBase>(json) ?? new DiscoveryKnowledgeBase();
                kb.GameDirectory = gameDirectory;
                kb.GameId = GetGameId(gameDirectory);
                return kb;
            }
            catch
            {
                return new DiscoveryKnowledgeBase { GameDirectory = gameDirectory, GameId = GetGameId(gameDirectory) };
            }
        }

        public static void Save(DiscoveryKnowledgeBase kb)
        {
            if (kb == null)
                return;
            try
            {
                kb.UpdatedUtc = DateTime.UtcNow;
                var path = GetKnowledgeBasePath(kb.GameDirectory ?? "");
                var json = JsonConvert.SerializeObject(kb, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }
    }
}
