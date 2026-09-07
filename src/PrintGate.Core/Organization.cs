namespace PrintGate.Core;

// Self-reported session metadata; never treated as a CAS-verified attribute.
public static class Organization
{
    public static string Normalize(string value)
    {
        var organization = value.Trim();
        if (organization.Length is 0 or > 100 || organization.Any(char.IsControl))
            throw new ArgumentException("请输入所属组织，长度为 1–100 个字符，不能包含换行等控制字符。");
        return organization;
    }
}
