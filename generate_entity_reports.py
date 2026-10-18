import os
from reportlab.lib.pagesizes import letter
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle

SRC = "Financial_Metrics_Benchmark_4_Entities.pdf"
E = {
    "A": dict(name="Apex Mfg Inc.", gross=4920000, ret=69800, net=4850200, off=210000, da=115300, intr=48250, ti=446900, st=350000, lt=900000, ebitda=820450),
    "B": dict(name="Nexus Logistics", gross=2350000, ret=39200, net=2310800, off=120000, da=45000, intr=23100, ti=157000, st=240000, lt=650000, ebitda=345100),
    "C": dict(name="Summit Healthcare", gross=6200000, ret=79500, net=6120500, off=250000, da=180000, intr=42000, ti=708000, st=250000, lt=900000, ebitda=1180000),
    "D": dict(name="Vantage Retail", gross=1910000, ret=60000, net=1850000, off=90000, da=35000, intr=45000, ti=15000, st=275000, lt=650000, ebitda=185000),
}


def d(v):
    return f"${v:,.0f}"


def p(v):
    return f"{v * 100:.2f}%"


ss = getSampleStyleSheet()
H1 = ParagraphStyle("h1", parent=ss["Title"], fontSize=20, spaceAfter=4)
H2 = ParagraphStyle("h2", parent=ss["Heading2"], textColor=colors.HexColor("#1F3A5F"), spaceBefore=12)
B = ss["BodyText"]
B.leading = 14
SM = ParagraphStyle("sm", parent=B, fontSize=8, textColor=colors.grey)


def table(rows, widths):
    t = Table(rows, colWidths=widths, repeatRows=1)
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1F3A5F")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
        ("FONTSIZE", (0, 0), (-1, -1), 9),
        ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#B0B7C3")),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F2F5F9")]),
        ("ALIGN", (1, 0), (-1, -1), "RIGHT"),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
    ]))
    return t


def narrative(k, x, m):
    lev, cov = m["lev"], m["cov"]
    txt = {
        "A": [
            f"Apex Mfg Inc. reports net revenue of {d(x['net'])} and recomputed EBITDA of {d(x['ebitda'])}, a {p(m['em'])} margin. Total debt of {d(m['debt'])} gives leverage of {lev:.2f}x, the second-lowest of the four entities.",
            f"Interest expense is small ({d(x['intr'])}, {p(m['ir'])} of revenue) and covered {cov:.1f}x by EBITDA. Debt is mostly long-term ({p(m['ltsh'])}), so near-term repayment pressure is limited.",
            f"Returns and allowances are {p(m['ra'])} of gross receipts. Taxable income is {p(m['nm'])} of net revenue, well below the EBITDA margin because of depreciation, interest and the officer compensation add-back.",
            f"Sensitivity: officer compensation ({d(x['off'])}) is {p(m['off_ebitda'])} of EBITDA. Without that add-back EBITDA is {d(m['ebitda_ex'])} and leverage rises to {m['lev_ex']:.2f}x."],
        "B": [
            f"Nexus Logistics reports net revenue of {d(x['net'])} and recomputed EBITDA of {d(x['ebitda'])}, a {p(m['em'])} margin. Total debt of {d(m['debt'])} gives leverage of {lev:.2f}x, the second-highest of the four entities.",
            f"Debt equals {p(m['dr'])} of revenue. Interest of {d(x['intr'])} is covered {cov:.1f}x by EBITDA, so servicing capacity is adequate at current earnings, but there is less headroom than at Entities A and C.",
            f"Taxable income is {p(m['nm'])} of net revenue. Returns and allowances are {p(m['ra'])} of gross receipts, the second-highest ratio in the group.",
            f"Sensitivity: officer compensation ({d(x['off'])}) is {p(m['off_ebitda'])} of EBITDA, the largest share of any entity but Entity D. Without that add-back EBITDA is {d(m['ebitda_ex'])} and leverage rises to {m['lev_ex']:.2f}x."],
        "C": [
            f"Summit Healthcare is the largest entity, with net revenue of {d(x['net'])} and recomputed EBITDA of {d(x['ebitda'])}, a {p(m['em'])} margin, the highest of the four. Total debt of {d(m['debt'])} gives leverage of {lev:.2f}x, below 1.0x and the lowest in the group.",
            f"Interest expense of {d(x['intr'])} is covered {cov:.1f}x by EBITDA. Debt is {p(m['dr'])} of revenue, and {p(m['ltsh'])} of it is long-term.",
            f"Taxable income is {p(m['nm'])} of net revenue, the strongest bottom-line margin. Returns and allowances are {p(m['ra'])} of gross receipts, the lowest of the four.",
            f"Sensitivity: officer compensation ({d(x['off'])}) is {p(m['off_ebitda'])} of EBITDA. Without that add-back EBITDA is {d(m['ebitda_ex'])} and leverage is {m['lev_ex']:.2f}x, still the lowest in the group."],
        "D": [
            f"Vantage Retail reports net revenue of {d(x['net'])} and recomputed EBITDA of only {d(x['ebitda'])}, a {p(m['em'])} margin. Total debt of {d(m['debt'])} gives leverage of {lev:.2f}x, far above the other three entities.",
            f"Debt equals {p(m['dr'])} of revenue. Interest expense ({d(x['intr'])}, {p(m['ir'])} of revenue) is the heaviest relative to size, and EBITDA covers it only {cov:.1f}x. Short-term debt is {p(m['stsh'])} of the total, the highest in the group.",
            f"Taxable income is {d(x['ti'])}, just {p(m['nm'])} of net revenue, so the entity is close to break-even on a tax basis. Returns and allowances are {p(m['ra'])} of gross receipts, the highest of the four.",
            f"Sensitivity: EBITDA depends heavily on add-backs. Officer compensation ({d(x['off'])}) is {p(m['off_ebitda'])} of EBITDA. Without it EBITDA is {d(m['ebitda_ex'])}, which would put leverage at {m['lev_ex']:.2f}x."],
    }
    return txt[k]


def build(k, x):
    debt = x["st"] + x["lt"]
    m = dict(debt=debt, lev=debt / x["ebitda"], cov=x["ebitda"] / x["intr"], em=x["ebitda"] / x["net"],
             nm=x["ti"] / x["net"], ra=x["ret"] / x["gross"], ir=x["intr"] / x["net"], dr=debt / x["net"],
             ltsh=x["lt"] / debt, stsh=x["st"] / debt, off_ebitda=x["off"] / x["ebitda"],
             ebitda_ex=x["ebitda"] - x["off"])
    m["lev_ex"] = debt / m["ebitda_ex"]
    assert x["gross"] - x["ret"] == x["net"]
    assert x["ti"] + x["intr"] + x["da"] + x["off"] == x["ebitda"]
    out = os.path.join("entity_reports", f"Entity_{k}_{x['name'].replace(' ', '_').replace('.', '')}_Report.pdf")
    doc = SimpleDocTemplate(out, pagesize=letter, leftMargin=54, rightMargin=54, topMargin=54, bottomMargin=54)
    s = [Paragraph(f"Entity {k}: {x['name']}", H1),
         Paragraph(f"Financial Profile Report, FY 2024-2025. Source: {SRC} (IRS Form 1120S/1065 line items and balance sheet). Risk score not calculated.", SM),
         Paragraph("1. Executive Summary", H2),
         Paragraph(narrative(k, x, m)[0], B),
         Spacer(1, 6),
         table([["Key metric", "Value"],
                ["Net revenue", d(x["net"])], ["Recomputed EBITDA", d(x["ebitda"])], ["Total debt", d(debt)],
                ["Debt / EBITDA", f"{m['lev']:.2f}x"], ["EBITDA margin", p(m["em"])],
                ["EBITDA / interest", f"{m['cov']:.1f}x"]], [250, 150]),
         Paragraph("2. Revenue Analysis", H2),
         table([["Line item", "Form line", "Amount"],
                ["Gross receipts / sales", "1a", d(x["gross"])], ["Returns & allowances", "1b", d(x["ret"])],
                ["Net revenue", "1c", d(x["net"])]], [220, 80, 100]),
         Spacer(1, 6),
         Paragraph(f"Returns and allowances reduce gross receipts by {p(m['ra'])}. Reconciliation check: {d(x['gross'])} - {d(x['ret'])} = {d(x['net'])}, which matches the reported Line 1c.", B),
         Paragraph("3. EBITDA Reconciliation", H2),
         table([["Component", "Form line", "Amount", "% of net revenue"],
                ["Taxable income / net", "21", d(x["ti"]), p(x["ti"] / x["net"])],
                ["+ Interest expense", "13", d(x["intr"]), p(x["intr"] / x["net"])],
                ["+ Depreciation & amortization", "14", d(x["da"]), p(x["da"] / x["net"])],
                ["+ Officer compensation", "7", d(x["off"]), p(x["off"] / x["net"])],
                ["= Recomputed EBITDA", "", d(x["ebitda"]), p(m["em"])]], [180, 70, 90, 100]),
         Spacer(1, 6),
         Paragraph("The benchmark formula adds officer compensation back to EBITDA, which is not standard EBITDA. The sensitivity in Section 6 shows the figures without that add-back.", B),
         Paragraph("4. Debt Structure and Leverage", H2),
         table([["Item", "Form line", "Amount", "Share of debt"],
                ["Short-term debt", "17d", d(x["st"]), p(m["stsh"])],
                ["Long-term debt", "19d", d(x["lt"]), p(m["ltsh"])],
                ["Total debt", "", d(debt), "100.00%"]], [180, 70, 90, 100]),
         Spacer(1, 6),
         Paragraph(f"Debt / EBITDA = {d(debt)} / {d(x['ebitda'])} = {m['lev']:.2f}x. Total debt is {p(m['dr'])} of net revenue.", B),
         Paragraph("5. Profitability and Coverage", H2),
         table([["Ratio", "Value"],
                ["EBITDA margin (EBITDA / net revenue)", p(m["em"])], ["Taxable income margin", p(m["nm"])],
                ["Interest expense / net revenue", p(m["ir"])], ["EBITDA / interest expense", f"{m['cov']:.1f}x"],
                ["Depreciation & amortization / net revenue", p(x["da"] / x["net"])],
                ["Officer compensation / net revenue", p(x["off"] / x["net"])]], [280, 120]),
         Paragraph("6. Analyst Commentary", H2)]
    s += [Paragraph("&bull; " + t, B) for t in narrative(k, x, m)[1:]]
    s += [Spacer(1, 6),
          table([["Sensitivity", "EBITDA", "Debt / EBITDA"],
                 ["As reported (with officer add-back)", d(x["ebitda"]), f"{m['lev']:.2f}x"],
                 ["Excluding officer compensation add-back", d(m["ebitda_ex"]), f"{m['lev_ex']:.2f}x"]], [230, 90, 90]),
          Paragraph("7. Data Limitations", H2),
          Paragraph("&bull; The source lists tax-return and balance-sheet figures only. No bank-statement totals are given, so tax-to-bank variance (data integrity) cannot be assessed.<br/>"
                    "&bull; No scheduled principal and interest payments are given, so debt service coverage (DSCR) is not calculated. EBITDA / interest is shown as a proxy.<br/>"
                    "&bull; The risk score and risk category are intentionally excluded from this report.", B)]
    doc.build(s)
    return out


os.makedirs("entity_reports", exist_ok=True)
for k, x in E.items():
    print(build(k, x))
