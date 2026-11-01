# -*- coding: utf-8 -*-
"""Material Symbols Outlined 字体子集生成器。

从下载的完整可变字体(约 10MB TTF,fonts.google.com 的 Material Symbols Outlined)
裁出 web 端实际用到的图标连字,产出几十 KB 的 woff2 放进 src/assets/fonts/。
新增图标时把名字追加到 ICONS 列表后重跑本脚本即可(需要 pip install fonttools brotli uharfbuzz)。

注意:图标的连字字形必须显式列入保留集 —— 只靠子集闭包会漏掉部分
(如 close),导致浏览器里显示成字母文本。

用法:python tools/subset-material-symbols.py <完整字体ttf路径>
"""
import sys
from pathlib import Path

from fontTools.subset import Options, Subsetter
from fontTools.ttLib import TTFont

# web 端用到的全部图标名(连字序列 = 图标名本身;模板里写 <span class="ms">名字</span>)
ICONS = """
menu more_vert dark_mode light_mode add folder folder_open computer description lock expand_less
expand_more arrow_right chevron_right arrow_back check check_circle cancel pending
radio_button_unchecked send stop push_pin keep_off archive settings tune
edit call_split delete undo compress summarize play_arrow pause view_sidebar
warning gpp_maybe content_copy refresh search terminal smart_toy
visibility visibility_off circle close download zoom_in zoom_out upload person
graphic_eq psychology live_tv theaters image wallpaper
photo videocam mic picture_as_pdf attach_file done remove
extension hub auto_awesome chevron_left info queue schedule drag_indicator unarchive paid
delete_sweep
drive_folder_upload create_new_folder forum
""".split()


def seq_name(first: str, comps, rev: dict) -> str:
    """连字图形序列 → 图标名(字母图形名即字母,下划线图形名为 underscore)。"""
    out = []
    for g in (first, *comps):
        if g == "underscore":
            out.append("_")
        else:
            ch = rev.get(g, g if len(g) == 1 else None)
            out.append(ch or "?")
    return "".join(out)


def each_lig_subtable(font: TTFont):
    for lookup in font["GSUB"].table.LookupList.Lookup:
        for st in lookup.SubTable:
            real = st.ExtSubTable if st.LookupType == 7 else st
            if getattr(real, "ligatures", None):
                yield real


def prune_ligatures(font: TTFont, rev: dict, kept: set) -> tuple[int, list]:
    """把 GSUB 连字裁到白名单。返回(裁掉数量, 保留连字的字形名列表)。"""
    pruned = 0
    lig_glyphs: list = []
    for real in each_lig_subtable(font):
        for first, arr in real.ligatures.items():
            keep = [lig for lig in arr if seq_name(first, lig.Component, rev) in kept]
            pruned += len(arr) - len(keep)
            arr[:] = keep
            lig_glyphs.extend(lig.LigGlyph for lig in keep)
    return pruned, lig_glyphs


def collect_names(font: TTFont, rev: dict) -> set:
    have = set()
    for real in each_lig_subtable(font):
        for first, arr in real.ligatures.items():
            for lig in arr:
                have.add(seq_name(first, lig.Component, rev))
    return have


def shape_one_glyph(font_path: Path, text: str) -> bool:
    """harfbuzz 验证:该名字必须整字连字成 1 个字形(否则浏览器会显示字母文本)。"""
    import uharfbuzz as hb

    blob = hb.Blob.from_file_path(str(font_path))
    font = hb.Font(hb.Face(blob))
    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(font, buf, {"kern": True, "liga": True})
    return len(buf.glyph_infos) == 1


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit("usage: python tools/subset-material-symbols.py <MaterialSymbolsOutlined-VariableFont.ttf>")
    src = Path(sys.argv[1])
    out_dir = Path(__file__).resolve().parent.parent / "src" / "assets" / "fonts"
    out_dir.mkdir(parents=True, exist_ok=True)
    font_out = out_dir / "MaterialSymbolsOutlined-Subset.woff2"

    font = TTFont(str(src))
    rev = {gn: chr(c) for c, gn in font.getBestCmap().items()}
    pruned, lig_glyphs = prune_ligatures(font, rev, set(ICONS))

    # 字母/下划线是连字的组成图形,必须保留;连字字形显式列入,闭包漏不掉
    opts = Options()
    opts.layout_features = ["*"]
    opts.name_IDs = [1, 2, 3, 4, 6]
    opts.notdef_outline = True
    opts.flavor = "woff2"
    ss = Subsetter(options=opts)
    ss.populate(text="abcdefghijklmnopqrstuvwxyz_")
    ss.populate(glyphs=sorted(set(lig_glyphs)))
    ss.subset(font)

    missing = [i for i in ICONS if i not in collect_names(font, rev)]
    font.save(str(font_out))
    (out_dir / "LICENSE.txt").write_bytes((src.parent / "LICENSE.txt").read_bytes())

    bad = [i for i in ICONS if not shape_one_glyph(font_out, i)]
    print(f"保留图标 {len(ICONS) - len(missing)}/{len(ICONS)},GSUB 缺失: {missing or '无'}")
    print(f"连字成型校验失败: {bad or '无'}")
    print(f"裁掉连字 {pruned} 条;输出 {font_out}({font_out.stat().st_size / 1024:.1f} KB)")
    if missing or bad:
        sys.exit(1)


if __name__ == "__main__":
    main()
