use super::*;
#[test]
fn bottom_panels_expand_upward_and_collapse_back_to_the_bottom() {
    let work = RECT {
        left: 0,
        top: 0,
        right: 2560,
        bottom: 1392,
    };
    let collapsed = RECT {
        left: 1390,
        top: 1344,
        right: 2086,
        bottom: 1392,
    };
    let expanded = resized_panel_bounds(collapsed, 354, work);
    assert_eq!((expanded.top, expanded.bottom), (1038, 1392));
    let folded = resized_panel_bounds(expanded, PANEL_HEADER_HEIGHT, work);
    assert_eq!((folded.top, folded.bottom), (1344, 1392));
}
#[test]
fn preserves_narrow_panels_and_collapsed_panels_at_the_bottom_edge() {
    let work = RECT {
        left: 0,
        top: 0,
        right: 2560,
        bottom: 1392,
    };
    let narrow = fit_panel_bounds(
        RECT {
            left: 0,
            top: 0,
            right: 249,
            bottom: 410,
        },
        work,
    );
    assert_eq!(
        (narrow.left, narrow.top, narrow.right, narrow.bottom),
        (0, 0, 249, 410)
    );
    let collapsed = fit_panel_bounds(
        RECT {
            left: 1390,
            top: 1344,
            right: 2086,
            bottom: 1392,
        },
        work,
    );
    assert_eq!(
        (
            collapsed.left,
            collapsed.top,
            collapsed.right,
            collapsed.bottom
        ),
        (1390, 1344, 2086, 1392)
    );
    let offscreen = fit_panel_bounds(
        RECT {
            left: 2700,
            top: 1500,
            right: 3140,
            bottom: 1900,
        },
        work,
    );
    assert_eq!(
        (
            offscreen.left,
            offscreen.top,
            offscreen.right,
            offscreen.bottom
        ),
        (2120, 992, 2560, 1392)
    );
}
