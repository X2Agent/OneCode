// OneCode WebUI 前端增强（零第三方依赖，仅本地静态资源）：
// 剪贴板复制 / 代码块复制按钮注入 / 流式自动滚动跟随 / 输入框自适应增高。
(function () {
    'use strict';

    /* ---------- 剪贴板 ---------- */

    function legacyCopy(text) {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        var ok = false;
        try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
        document.body.removeChild(ta);
        return ok;
    }

    function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return legacyCopy(text); });
        }
        return Promise.resolve(legacyCopy(text));
    }

    /* ---------- 代码块复制按钮（完成态消息才注入；流式中的代码块跳过） ---------- */

    var COPY_SVG = '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/><path d="M10.5 3.5h-7a1 1 0 0 0-1 1v7"/></svg>';
    var CHECK_SVG = '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8.5l3.2 3.2L13 5"/></svg>';

    function enhanceCodeBlocks() {
        var nodes = document.querySelectorAll('.markdown pre:not([data-copy])');
        nodes.forEach(function (pre) {
            if (pre.closest('.message.streaming')) return;
            pre.setAttribute('data-copy', '1');
            var text = pre.textContent;
            var btn = document.createElement('button');
            btn.className = 'code-copy';
            btn.type = 'button';
            btn.title = '复制代码';
            btn.innerHTML = COPY_SVG;
            btn.addEventListener('click', function () {
                copyText(text).then(function (ok) {
                    if (!ok || btn.classList.contains('copied')) return;
                    btn.classList.add('copied');
                    btn.innerHTML = CHECK_SVG;
                    setTimeout(function () {
                        btn.classList.remove('copied');
                        btn.innerHTML = COPY_SVG;
                    }, 2500);
                });
            });
            pre.appendChild(btn);
        });
    }

    /* ---------- 流式自动滚动跟随：贴底跟随，上滚即停，回底恢复 ---------- */

    function initAutoScroll() {
        var el = document.querySelector('.transcript');
        if (!el) return;
        var follow = true;
        var sticky = 48;
        el.addEventListener('scroll', function () {
            follow = el.scrollHeight - el.scrollTop - el.clientHeight < sticky;
        }, { passive: true });
        new MutationObserver(function () {
            if (follow) el.scrollTop = el.scrollHeight;
            enhanceCodeBlocks();
        }).observe(el, { childList: true, subtree: true, characterData: true });
    }

    /* ---------- 输入框自适应增高 ---------- */

    var composer = null;

    function fitComposer() {
        if (!composer) composer = document.querySelector('.composer textarea');
        if (!composer) return;
        composer.style.height = 'auto';
        composer.style.height = Math.min(composer.scrollHeight, 200) + 'px';
    }

    function initComposer() {
        if (!composer) composer = document.querySelector('.composer textarea');
        if (composer) composer.addEventListener('input', fitComposer);
    }

    function init() {
        initAutoScroll();
        initComposer();
        enhanceCodeBlocks();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    window.app = {
        copyText: copyText,
        fitComposer: fitComposer
    };
})();
