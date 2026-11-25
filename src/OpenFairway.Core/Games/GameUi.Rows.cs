using System;
using OpenFairway.API;

namespace OpenFairway.Core.Games;

/// <summary>
/// The main menu of PGA TOUR 2K23 (<see cref="MenuStyle.Rows"/>): a bar of tabs along the bottom, each a short list
/// of text rows under the tab's name drawn large as an image. Rows change on screen as soon as a tab's list does, so
/// only the tab names and the title image need looking after here.
/// </summary>
internal sealed unsafe partial class GameUi
{
    private const string TabListPath = "Footer/Panel List/List";
    private const string TabLabel = "ButtonText", TabLabelHighlighted = "ButtonTextHighlight";
    private const string TabTitlePath = "TabButtonPanel/PanelName";

    /// <summary>Share of the title box's width that the game's own title art fills, right-aligned (measured on its
    /// MyPLAYER title).</summary>
    private const float TitleArtWidth = 0.55f;

    private const float TitleFitTolerance = 0.01f;

    /// <summary>The title image's texture coordinates were changed to fit the wordmark and must be put back for
    /// the other tabs' own titles.</summary>
    private bool _titleRefitted;

    /// <summary>Width over height of the title box the wordmark was last fitted to. The game reshapes the box to the
    /// proportions of the image it shows, a moment after the image changes, so the fit is redone when it does.</summary>
    private float _titleBoxFitted;

    private void ApplyRowView(IntPtr screenRoot, IntPtr panelsModel, IntPtr[] currentTabs)
    {
        foreach (var (tab, name) in _layout.TabNames) RenameTab(screenRoot, currentTabs, tab, name);
        if (_banner?.LogoPath != null) ApplyTitleLogo(screenRoot, panelsModel, currentTabs, _banner.LogoPath);
    }

    /// <summary>Names a tab in the tab bar: in its model, for a bar built later, and on the bar already on screen.</summary>
    private void RenameTab(IntPtr screenRoot, IntPtr[] currentTabs, MenuTab tab, string name)
    {
        if (!_layout.Tabs.TryGetValue(tab, out var index)) return;
        var model = _menu!.Tabs[index];
        var position = Array.IndexOf(currentTabs, model);
        if (position < 0) return;
        if (NameText(model) != name) Relabel(model, name, null);
        var list = Find(screenRoot, TabListPath);
        if (list == IntPtr.Zero || position >= ChildCount(list)) return;
        var button = GetChild(list, position);
        SetText(Find(button, TabLabel), name);
        SetText(Find(button, TabLabelHighlighted), name);
    }

    /// <summary>While the HOME tab is in front, its large title shows the banner's wordmark instead.</summary>
    private void ApplyTitleLogo(IntPtr screenRoot, IntPtr panelsModel, IntPtr[] currentTabs, string path)
    {
        if (_getTexture == IntPtr.Zero || _setUvRect == IntPtr.Zero || _getRect == IntPtr.Zero) return;
        var title = Find(screenRoot, TabTitlePath);
        if (title == IntPtr.Zero || !LoadBannerLogo(path)) return;
        var image = _rt.Invoke(_getComponent, GameObject(title), _rawImageType);
        if (image == IntPtr.Zero) return;
        var highlighted = Int(_rt.Invoke(_getHighlightedIndex, panelsModel));
        var onHome = highlighted >= 0 && highlighted < currentTabs.Length && TabOf(currentTabs[highlighted]) == MenuTab.Home;
        var showsLogo = _rt.Invoke(_getTexture, image) == _bannerLogo;

        if (onHome)
        {
            if (!showsLogo)
            {
                Step("title wordmark");
                _rt.Invoke(_setTexture, image, _bannerLogo);
                _titleBoxFitted = 0f;
            }
            // The game's own title art covers the right part of a wide box. Put the wordmark there, in its own
            // proportions, by sampling past the image's clear edges. Rect { x, y, width, height }: the box as laid out.
            var rect = _rt.Invoke(_getRect, title);
            var box = Math.Max(0.01f, *(float*)(rect + 24) / Math.Max(1f, *(float*)(rect + 28)));
            if (Math.Abs(box - _titleBoxFitted) < TitleFitTolerance) return;
            _titleBoxFitted = box;
            var width = TitleArtWidth;
            var height = width * box / _bannerLogoAspect;
            if (height > 1f) (width, height) = (width / height, 1f);
            var uv = stackalloc float[4];
            uv[2] = 1f / width;
            uv[3] = 1f / height;
            uv[0] = 1f - uv[2];          // right edges together
            uv[1] = -(uv[3] - 1f) / 2f;  // centred top to bottom
            _rt.Invoke(_setUvRect, image, (IntPtr)uv);
            _titleRefitted = true;
        }
        else if (!showsLogo && _titleRefitted)
        {
            var whole = stackalloc float[4] { 0f, 0f, 1f, 1f };
            _rt.Invoke(_setUvRect, image, (IntPtr)whole);
            _titleRefitted = false;
        }
    }
}
