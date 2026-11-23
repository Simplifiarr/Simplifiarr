using Simplifiarr.Core.Models;

namespace Simplifiarr.Tests;

[TestClass]
public class EncodeRulesTests
{
    [TestMethod]
    public void Words_are_matched_whole_ignoring_dots_and_dashes()
    {
        Assert.IsTrue(EncodeRules.HasWord("Film.2020.CAM.x264-GRP", "cam"));
        Assert.IsFalse(EncodeRules.HasWord("Film.Camera.2020.1080p", "CAM"));
        Assert.IsTrue(EncodeRules.HasWord("Film.2020.Hard-Coded.Subs", "hard coded"));
        Assert.IsFalse(EncodeRules.HasWord("Film.2020", "   "));
    }

    [TestMethod]
    public void Codecs_are_known_by_any_of_their_names()
    {
        Assert.AreEqual("HEVC", EncodeRules.CodecOf("Film.2020.1080p", "X265"));
        Assert.AreEqual("HEVC", EncodeRules.CodecOf("Film.2020.1080p.H.265-GRP", null));
        Assert.AreEqual("H264", EncodeRules.CodecOf("Film.2020.1080p.AVC-GRP", ""));
        Assert.AreEqual(string.Empty, EncodeRules.CodecOf("Film.2020.1080p-GRP", null));
    }

    [TestMethod]
    public void With_codecs_ticked_a_release_that_does_not_say_is_allowed_only_if_asked()
    {
        var rules = new EncodeRules { Codecs = ["AV1"], AvoidWords = [] };

        Assert.IsNull(rules.WhyLeftOut("Film.2020.1080p.AV1-GRP", "AV1"));
        Assert.IsNull(rules.WhyLeftOut("Film.2020.1080p-GRP", null));

        rules.AllowUnstatedCodec = false;

        Assert.AreEqual("Codec not stated", rules.WhyLeftOut("Film.2020.1080p-GRP", null));
        Assert.AreEqual("HEVC / x265 isn't a codec you want", rules.WhyLeftOut("Film.2020.1080p.x265-GRP", "X265"));
    }

    [TestMethod]
    public void A_new_install_leaves_out_cam_copies_and_takes_any_codec()
    {
        var rules = new EncodeRules();

        Assert.IsNull(rules.WhyLeftOut("Film.2020.1080p.WEB.x264-GRP", "X264"));
        Assert.AreEqual("Has \"TELESYNC\"", rules.WhyLeftOut("Film.2020.TELESYNC.x264-GRP", "X264"));
    }

    [TestMethod]
    public void Typed_words_are_tidied()
    {
        Assert.AreEqual("WEB DL", EncodeRules.NormalizeWord("  web.dl ".ToUpperInvariant()));
        Assert.IsNull(EncodeRules.NormalizeWord(" - . "));
    }
}
