using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class AssetMatchAnalysisService
    : IAssetMatchAnalysisService
{
    public IReadOnlyCollection<AssetMatchAnalysis> Analyze(
        IEnumerable<MediaAsset> assets,
        IEnumerable<TorrentRecord> torrents,
        IEnumerable<AssetTorrentMatch> matches)
    {
        var analyses =
            new List<AssetMatchAnalysis>();

        foreach (var asset in assets)
        {
            var assetMatches =
                matches
                    .Where(match =>
                        match.MediaAssetId == asset.Id)
                    .ToList();

            if (assetMatches.Count == 0)
            {
                analyses.Add(
                    new AssetMatchAnalysis
                    {
                        MediaAssetId = asset.Id,
                        AssetTitle = asset.Title,
                        AssetYear = asset.Year,
                        IsMatched = false,

                        RelationshipType =
                            AssetRelationshipType.LibraryOnly,

                        CandidateCount = 0,

                        Reason = "No torrent candidates found"
                    });

                continue;
            }

            if (assetMatches.Count > 1)
            {
                var rankedCandidates =
                    assetMatches
                        .OrderByDescending(m => m.Confidence)
                        .ThenBy(m => m.SizeDifferenceBytes)
                        .ToList();

                var bestCandidate =
                    rankedCandidates[0];

                var secondBestScore =
                    rankedCandidates.Count > 1
                        ? rankedCandidates[1].Confidence
                        : 0;

                analyses.Add(
                    new AssetMatchAnalysis
                    {
                        MediaAssetId = asset.Id,
                        AssetTitle = asset.Title,
                        AssetYear = asset.Year,
                        IsMatched =
                            bestCandidate.Confidence >= 80,

                        RelationshipType =
                            bestCandidate.Confidence >= 80
                                ? AssetRelationshipType.Matched
                                : AssetRelationshipType.Candidate,

                        CandidateCount = assetMatches.Count,
                        Reason =
                            bestCandidate.Confidence >= 80
                                ? "Multiple high-confidence candidates found"
                                : "Candidates found but none meet match threshold",

                        BestCandidateName =
                            bestCandidate.TorrentName,

                        BestCandidateScore =
                            bestCandidate.Confidence,

                        SecondBestCandidateScore =
                            secondBestScore
                    });

                continue;
            }

            analyses.Add(
                new AssetMatchAnalysis
                {
                    MediaAssetId = asset.Id,
                    AssetTitle = asset.Title,
                    AssetYear = asset.Year,
                    IsMatched =
                        assetMatches[0].Confidence >= 80,

                    RelationshipType =
                        assetMatches[0].Confidence >= 80
                            ? AssetRelationshipType.Matched
                            : AssetRelationshipType.Candidate,

                    CandidateCount = 1,
                    Reason =
                        assetMatches[0].Confidence >= 80
                            ? "Matched"
                            : "Candidate found but below match threshold",

                    BestCandidateName =
                        assetMatches[0].TorrentName,

                    BestCandidateScore =
                        assetMatches[0].Confidence
                });
        }

        return analyses;
    }
}





