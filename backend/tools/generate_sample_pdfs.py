"""Generates digital sample PDFs (text layer) for the four benchmark entities.
The labelled-line format is what the offline stub extractor understands. Real models read any layout."""
import os
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "samples")
ENT = {
    "Apex_Mfg_Inc": ("Apex Mfg Inc.", 4920000, 69800, 4850200, 210000, 115300, 48250, 446900, 350000, 900000, 4912400),
    "Nexus_Logistics": ("Nexus Logistics", 2350000, 39200, 2310800, 120000, 45000, 23100, 157000, 240000, 650000, None),
    "Summit_Healthcare": ("Summit Healthcare", 6200000, 79500, 6120500, 250000, 180000, 42000, 708000, 250000, 900000, None),
    "Vantage_Retail": ("Vantage Retail", 1910000, 60000, 1850000, 90000, 35000, 45000, 15000, 275000, 650000, None),
}
os.makedirs(OUT, exist_ok=True)
for key, (name, gross, ret, net, off, da, intr, ti, st, lt, bank) in ENT.items():
    c = canvas.Canvas(os.path.join(OUT, f"{key}_FY2024-25.pdf"), pagesize=letter)
    y = 740
    def line(t, size=11):
        global y
        c.setFont("Helvetica", size); c.drawString(60, y, t); y -= 20
    line(f"{name} - Financial extraction FY 2024-2025", 14); y -= 10
    line("Source: IRS Form 1120S tax return and bank statements")
    line(f"Gross receipts / sales (Form 1120S Line 1a): ${gross:,}")
    line(f"Returns and allowances (Line 1b): ${ret:,}")
    line(f"Net revenue (Line 1c): ${net:,}")
    line(f"Officer compensation (Line 7): ${off:,}")
    c.showPage(); y = 740
    line(f"Depreciation and amortization (Line 14): ${da:,}")
    line(f"Interest expense (Line 13): ${intr:,}")
    line(f"Taxable income / net (Line 21): ${ti:,}")
    line(f"Short-term debt, notes payable current (Balance Sheet Line 17d): ${st:,}")
    line(f"Long-term debt, mortgages and notes payable (Balance Sheet Line 19d): ${lt:,}")
    if bank: line(f"Bank statement total credits: ${bank:,}")
    c.save()
print("written to", os.path.abspath(OUT))
