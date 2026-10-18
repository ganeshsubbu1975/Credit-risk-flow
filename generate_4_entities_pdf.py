from reportlab.lib import colors
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.platypus import HRFlowable, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

def build_pdf(filename="Financial_Metrics_Benchmark_4_Entities.pdf"):
    doc = SimpleDocTemplate(
        filename,
        pagesize=letter,
        leftMargin=36,
        rightMargin=36,
        topMargin=36,
        bottomMargin=36
    )
    
    styles = getSampleStyleSheet()
    
    # Custom styles
    title_style = ParagraphStyle(
        'DocTitle',
        parent=styles['Heading1'],
        fontName='Helvetica-Bold',
        fontSize=18,
        leading=22,
        textColor=colors.HexColor('#1A2B4C'),
        spaceAfter=4
    )
    
    subtitle_style = ParagraphStyle(
        'DocSubtitle',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=9,
        leading=12,
        textColor=colors.HexColor('#4A5568'),
        spaceAfter=8
    )
    
    section_heading = ParagraphStyle(
        'SectionHeading',
        parent=styles['Heading2'],
        fontName='Helvetica-Bold',
        fontSize=12,
        leading=15,
        textColor=colors.HexColor('#1A2B4C'),
        spaceBefore=10,
        spaceAfter=6
    )
    
    cell_bold = ParagraphStyle(
        'CellBold',
        parent=styles['Normal'],
        fontName='Helvetica-Bold',
        fontSize=8,
        leading=10,
        textColor=colors.HexColor('#1A2B4C')
    )
    
    cell_normal = ParagraphStyle(
        'CellNormal',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=8,
        leading=10,
        textColor=colors.HexColor('#2D3748')
    )

    story = []

    # Title & Header Block
    story.append(Paragraph("Financial Extraction Benchmark Dataset", title_style))
    story.append(Paragraph("Ground Truth Metrics Extracted from Tax Returns & Bank Statements (FY 2024-2025) | Model Target: GPT-4o-mini", subtitle_style))
    story.append(HRFlowable(width="100%", thickness=1.5, color=colors.HexColor('#1A2B4C'), spaceAfter=10))

    # SECTION 1: Consolidated Summary (4 Entities)
    story.append(Paragraph("Summary KPI Overview — Consolidated 4-Entity Ground Truth", section_heading))
    
    kpi_data = [
        [
            Paragraph("Metric / Entity", cell_bold),
            Paragraph("Entity A<br/>Apex Mfg Inc.", cell_bold),
            Paragraph("Entity B<br/>Nexus Logistics", cell_bold),
            Paragraph("Entity C<br/>Summit Healthcare", cell_bold),
            Paragraph("Entity D<br/>Vantage Retail", cell_bold)
        ],
        [
            Paragraph("Gross Revenue", cell_normal),
            Paragraph("$4,850,200", cell_normal),
            Paragraph("$2,310,800", cell_normal),
            Paragraph("$6,120,500", cell_normal),
            Paragraph("$1,850,000", cell_normal)
        ],
        [
            Paragraph("Total Debt", cell_normal),
            Paragraph("$1,250,000", cell_normal),
            Paragraph("$890,000", cell_normal),
            Paragraph("$1,150,000", cell_normal),
            Paragraph("$925,000", cell_normal)
        ],
        [
            Paragraph("Calculated EBITDA", cell_normal),
            Paragraph("$820,450", cell_normal),
            Paragraph("$345,100", cell_normal),
            Paragraph("$1,180,000", cell_normal),
            Paragraph("$185,000", cell_normal)
        ],
        [
            Paragraph("Debt / EBITDA Ratio", cell_bold),
            Paragraph("1.52x", cell_bold),
            Paragraph("2.58x", cell_bold),
            Paragraph("0.97x", cell_bold),
            Paragraph("5.00x", cell_bold)
        ],
        [
            Paragraph("Risk Score (0-100)", cell_bold),
            Paragraph("90.5 / 100", cell_bold),
            Paragraph("78.6 / 100", cell_bold),
            Paragraph("94.2 / 100", cell_bold),
            Paragraph("52.1 / 100", cell_bold)
        ],
        [
            Paragraph("Risk Profile Category", cell_bold),
            Paragraph("Prime / Low Risk", cell_normal),
            Paragraph("Moderate Risk", cell_normal),
            Paragraph("Prime / Low Risk", cell_normal),
            Paragraph("Elevated / High Risk", cell_normal)
        ]
    ]

    t_kpi = Table(kpi_data, colWidths=[110, 105, 105, 110, 110])
    t_kpi.setStyle(TableStyle([
        ('BACKGROUND', (0,0), (-1,0), colors.HexColor('#EDF2F7')),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#CBD5E0')),
        ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('BOTTOMPADDING', (0,0), (-1,-1), 5),
        ('TOPPADDING', (0,0), (-1,-1), 5),
    ]))
    story.append(t_kpi)
    story.append(Spacer(1, 12))

    # SECTION 2: Detailed Line Item Mapping for GPT-4o-mini Testing
    story.append(Paragraph("Detailed Extraction Test Items — Entity Breakdown Matrix", section_heading))

    # White text style for header row of detailed table
    header_style = ParagraphStyle('HeaderStyle', parent=styles['Normal'], fontName='Helvetica-Bold', fontSize=8, leading=10, textColor=colors.white)

    detail_data = [
        [
            Paragraph("Metric Description", header_style),
            Paragraph("Source Document", header_style),
            Paragraph("Line Item", header_style),
            Paragraph("Entity A", header_style),
            Paragraph("Entity B", header_style),
            Paragraph("Entity C", header_style),
            Paragraph("Entity D", header_style)
        ],
        [
            Paragraph("Gross Receipts / Sales", cell_normal),
            Paragraph("IRS Form (1120S/1065)", cell_normal),
            Paragraph("Line 1a", cell_normal),
            Paragraph("$4,920,000", cell_normal),
            Paragraph("$2,350,000", cell_normal),
            Paragraph("$6,200,000", cell_normal),
            Paragraph("$1,910,000", cell_normal)
        ],
        [
            Paragraph("Returns & Allowances", cell_normal),
            Paragraph("IRS Form (1120S/1065)", cell_normal),
            Paragraph("Line 1b", cell_normal),
            Paragraph("$69,800", cell_normal),
            Paragraph("$39,200", cell_normal),
            Paragraph("$79,500", cell_normal),
            Paragraph("$60,000", cell_normal)
        ],
        [
            Paragraph("Net Revenue", cell_normal),
            Paragraph("IRS Form (1120S/1065)", cell_normal),
            Paragraph("Line 1c", cell_normal),
            Paragraph("$4,850,200", cell_normal),
            Paragraph("$2,310,800", cell_normal),
            Paragraph("$6,120,500", cell_normal),
            Paragraph("$1,850,000", cell_normal)
        ],
        [
            Paragraph("Officer Compensation", cell_normal),
            Paragraph("IRS Form / Payroll", cell_normal),
            Paragraph("Line 7", cell_normal),
            Paragraph("$210,000", cell_normal),
            Paragraph("$120,000", cell_normal),
            Paragraph("$250,000", cell_normal),
            Paragraph("$90,000", cell_normal)
        ],
        [
            Paragraph("Depreciation & Amort.", cell_normal),
            Paragraph("IRS Form (1120S/1065)", cell_normal),
            Paragraph("Line 14", cell_normal),
            Paragraph("$115,300", cell_normal),
            Paragraph("$45,000", cell_normal),
            Paragraph("$180,000", cell_normal),
            Paragraph("$35,000", cell_normal)
        ],
        [
            Paragraph("Interest Expense", cell_normal),
            Paragraph("IRS Form / Debt Debits", cell_normal),
            Paragraph("Line 13", cell_normal),
            Paragraph("$48,250", cell_normal),
            Paragraph("$23,100", cell_normal),
            Paragraph("$42,000", cell_normal),
            Paragraph("$45,000", cell_normal)
        ],
        [
            Paragraph("Taxable Income / Net", cell_normal),
            Paragraph("IRS Form (1120S/1065)", cell_normal),
            Paragraph("Line 21", cell_normal),
            Paragraph("$446,900", cell_normal),
            Paragraph("$157,000", cell_normal),
            Paragraph("$708,000", cell_normal),
            Paragraph("$15,000", cell_normal)
        ],
        [
            Paragraph("Short-Term Debt", cell_normal),
            Paragraph("Balance Sheet (L)", cell_normal),
            Paragraph("Line 17d", cell_normal),
            Paragraph("$350,000", cell_normal),
            Paragraph("$240,000", cell_normal),
            Paragraph("$250,000", cell_normal),
            Paragraph("$275,000", cell_normal)
        ],
        [
            Paragraph("Long-Term Debt", cell_normal),
            Paragraph("Balance Sheet (L)", cell_normal),
            Paragraph("Line 19d", cell_normal),
            Paragraph("$900,000", cell_normal),
            Paragraph("$650,000", cell_normal),
            Paragraph("$900,000", cell_normal),
            Paragraph("$650,000", cell_normal)
        ]
    ]

    t_detail = Table(detail_data, colWidths=[100, 95, 55, 72, 72, 73, 73])
    t_detail.setStyle(TableStyle([
        ('BACKGROUND', (0,0), (-1,0), colors.HexColor('#2B6CB0')),
        ('TEXTCOLOR', (0,0), (-1,0), colors.white),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#E2E8F0')),
        ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('BOTTOMPADDING', (0,0), (-1,-1), 4),
        ('TOPPADDING', (0,0), (-1,-1), 4),
    ]))

    story.append(t_detail)

    # PAGE 2
    story.append(PageBreak())

    story.append(Paragraph("Reconciliation & Risk Score Calculation Methodology", section_heading))
    story.append(HRFlowable(width="100%", thickness=1, color=colors.HexColor('#CBD5E0'), spaceAfter=8))

    formula_text = """
    <b>EBITDA Recomputed Formula:</b><br/>
    EBITDA = Net Taxable Income (Line 21) + Interest Expense (Line 13) + Depreciation (Line 14) + Officer Compensation (Line 7)<br/><br/>
    <b>Entity Calculations:</b><br/>
    • <b>Entity A:</b> $446,900 + $48,250 + $115,300 + $210,000 = <b>$820,450</b> | Debt = $1,250,000 | Leverage = <b>1.52x</b><br/>
    • <b>Entity B:</b> $157,000 + $23,100 + $45,000 + $120,000 = <b>$345,100</b> | Debt = $890,000 | Leverage = <b>2.58x</b><br/>
    • <b>Entity C:</b> $708,000 + $42,000 + $180,000 + $250,000 = <b>$1,180,000</b> | Debt = $1,150,000 | Leverage = <b>0.97x</b><br/>
    • <b>Entity D:</b> $15,000 + $45,000 + $35,000 + $90,000 = <b>$185,000</b> | Debt = $925,000 | Leverage = <b>5.00x</b><br/>
    """
    story.append(Paragraph(formula_text, cell_normal))
    story.append(Spacer(1, 10))

    story.append(Paragraph("Multi-Factor Risk Score Weighting Model (0 - 100)", section_heading))
    
    weight_data = [
        [Paragraph("Pillar Metric", cell_bold), Paragraph("Weight", cell_bold), Paragraph("Scoring Criterion", cell_bold)],
        [Paragraph("Leverage Ratio (Debt / EBITDA)", cell_normal), Paragraph("40%", cell_normal), Paragraph("< 1.5x = 95-100 | 1.5-2.5x = 80-94 | 2.5-4.0x = 60-79 | > 4.0x = < 60", cell_normal)],
        [Paragraph("Debt Service Coverage (DSCR)", cell_normal), Paragraph("30%", cell_normal), Paragraph("> 10x = 95-100 | 5-10x = 80-94 | 2-5x = 60-79 | < 2x = < 60", cell_normal)],
        [Paragraph("Profit Margin (EBITDA / Rev)", cell_normal), Paragraph("20%", cell_normal), Paragraph("> 20% = 90-100 | 15-20% = 80-89 | 10-15% = 70-79 | < 10% = < 60", cell_normal)],
        [Paragraph("Data Integrity / Variance", cell_normal), Paragraph("10%", cell_normal), Paragraph("< 1% Tax-to-Bank Variance = 95-100 | > 5% Variance = Penalized", cell_normal)]
    ]

    t_weight = Table(weight_data, colWidths=[150, 60, 330])
    t_weight.setStyle(TableStyle([
        ('BACKGROUND', (0,0), (-1,0), colors.HexColor('#EDF2F7')),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#CBD5E0')),
        ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('BOTTOMPADDING', (0,0), (-1,-1), 5),
        ('TOPPADDING', (0,0), (-1,-1), 5),
    ]))
    story.append(t_weight)

    doc.build(story)
    print(f"PDF successfully built: {filename}")

if __name__ == "__main__":
    build_pdf()