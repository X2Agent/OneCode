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

    // 预渲染 DOM 会被交互式渲染接管时整体替换，因此监听挂在 body / document 上，
    // 每轮变更重新定位 .transcript，不持有跨渲染周期的节点引用。
    var STICKY = 48;
    var follow = true;
    var transcript = null;

    function syncTranscript() {
        var el = document.querySelector('.transcript');
        if (!el) return;

        // 元素被替换（预渲染接管、组件重建）：回到贴底跟随。
        if (el !== transcript) {
            transcript = el;
            follow = true;
        }

        // 内容不足以滚动说明刚被清空重放（切换会话）：同样恢复跟随。
        if (el.scrollHeight <= el.clientHeight) follow = true;

        if (follow) el.scrollTop = el.scrollHeight;
    }

    /* ---------- 会话列表：触底加载，且未撑满视口时自动补屏 ---------- */

    var sessionDotnet = null;
    var sessionLoading = false;

    function requestMoreSessions() {
        if (!sessionDotnet || sessionLoading) return;
        sessionLoading = true;
        sessionDotnet.invokeMethodAsync('LoadMore')
            .then(function () {
                sessionLoading = false;
                // 渲染完成后复检：一屏仍没把列表撑出滚动条时继续补。
                setTimeout(fillSessionList, 120);
            })
            .catch(function () { sessionLoading = false; });
    }

    // 高视口下 10 条不足以产生滚动条，触底回调永远等不到——先补到能滚为止。
    function fillSessionList() {
        var list = document.querySelector('.session-list');
        if (!list || !sessionDotnet) return;
        if (!list.querySelector('.session-sentinel')) return;      // 已全部展开
        if (list.scrollHeight > list.clientHeight + 1) return;     // 已可滚动，交给触底回调
        requestMoreSessions();
    }

    /* ---------- 输入框自适应增高 ---------- */

    function fitComposer() {
        var el = document.querySelector('.composer textarea');
        if (!el) return;
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 200) + 'px';
    }

    function init() {
        new MutationObserver(function () {
            syncTranscript();
            fillSessionList();
            enhanceCodeBlocks();
        }).observe(document.body, { childList: true, subtree: true, characterData: true });

        // 滚动事件不冒泡，用捕获阶段接管：上滚即停，回底恢复；会话列表触底翻下一屏。
        document.addEventListener('scroll', function (e) {
            var el = e.target;
            if (!(el instanceof Element)) return;
            if (el.classList.contains('transcript')) {
                follow = el.scrollHeight - el.clientHeight - el.scrollTop <= STICKY;
            } else if (el.classList.contains('session-list') &&
                el.scrollHeight - el.clientHeight - el.scrollTop <= STICKY) {
                requestMoreSessions();
            }
        }, true);

        // 视口变高后列表可能重新变成不可滚动，需要补屏。
        window.addEventListener('resize', function () { fillSessionList(); });

        // 输入事件同样走委托，不依赖节点引用。
        document.addEventListener('input', function (e) {
            if (e.target instanceof HTMLElement && e.target.closest('.composer')) fitComposer();
        });

        enhanceCodeBlocks();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    window.app = {
        copyText: copyText,
        fitComposer: fitComposer,
        bindSessionScroll: function (dotnet) { sessionDotnet = dotnet; fillSessionList(); },
        unbindSessionScroll: function () { sessionDotnet = null; }
    };
})();
