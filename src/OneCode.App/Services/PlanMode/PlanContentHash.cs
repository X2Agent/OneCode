using System.Security.Cryptography;
using System.Text;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 正文内容哈希的唯一算法出处：revision 落盘与「重复提交」判重必须同源，
/// 否则两处各写一份 SHA256 迟早漂移，幂等判据就会静默失效。
/// </summary>
internal static class PlanContentHash
{
    public static string Of(string markdown)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();
}
