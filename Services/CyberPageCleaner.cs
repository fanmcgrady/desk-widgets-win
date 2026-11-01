namespace DeskWidgets.Services;

/// <summary>网安详情页：藏掉浮动层和常见广告，保留正文。</summary>
public static class CyberPageCleaner
{
    public const string Script =
        """
        (function () {
          if (window.__dwClean) return;
          window.__dwClean = 1;
          if (window.top !== window.self) return;

          var css = document.createElement('style');
          css.setAttribute('data-dw-clean', '1');
          css.textContent = [
            'html,body{overflow-x:hidden!important;max-width:100%!important;}',
            '[data-dw-hide]{display:none!important;}',
            'ins.adsbygoogle,iframe[src*="doubleclick"],iframe[src*="googlesyndication"],',
            'iframe[src*="adservice"],iframe[id^="google_ads"],[id^="div-gpt-ad"],',
            '[id^="google_ads_iframe"],.adsbygoogle,',
            '.modal-backdrop,.ant-modal-mask,.ant-modal-wrap,.el-overlay,.v-modal,',
            '[class*="adsbygoogle"],',
            '.floating-view,.floating-view-item,.link-badge-item-fixed,.mutual-fixed-wraper,',
            '#link-wrapper-container,[class*="floating-view"]{display:none!important;}'
          ].join('');
          (document.head || document.documentElement).appendChild(css);

          var artSel = 'article,[role="main"],.article-content,.post-content,.entry-content,.artical-content,#artical-content,.content-detail,.article_content,.news-content,.post-body,.article-body,.detail-content,.markdown-body,.post-detail,.blog-content';

          function findArticle() {
            return document.querySelector(artSel);
          }

          function nameOf(el) {
            var cls = typeof el.className === 'string' ? el.className : '';
            return ((el.id || '') + ' ' + cls).toLowerCase();
          }

          function isAdEl(el) {
            if (!el || el.nodeType !== 1) return false;
            var tag = el.tagName;
            if (tag === 'INS' && /adsbygoogle/i.test(nameOf(el))) return true;
            if (tag === 'IFRAME') {
              var src = (el.getAttribute('src') || '') + ' ' + (el.getAttribute('id') || '');
              if (/doubleclick|googlesyndication|adservice|adnxs|taboola|outbrain|popin\.|pos\.baidu|cpro\.baidu|tanx\.com|alimama|googletagservices/i.test(src))
                return true;
            }
            if (el.getAttribute('data-ad') || el.getAttribute('data-ad-slot') || el.getAttribute('data-google-query-id'))
              return true;
            if (/(^|[^a-z0-9])(ads?|ad-box|adbox|adslot|advert|adsbygoogle|guanggao|sponsor|banner-ad)([^a-z0-9]|$)/.test(nameOf(el)))
              return true;
            return isFloatChrome(el);
          }

          function isFloatChrome(el) {
            if (el.id === 'link-wrapper-container') return true;
            return /floating-view|link-badge-item-fixed|mutual-fixed|qrcode-box|qrcode-xcx|float-ad|fixed-ad|suspension/.test(nameOf(el));
          }

          function isPromoFloat(el, s, r, text) {
            if (!s || (s.position !== 'fixed' && s.position !== 'sticky')) return false;
            if (r.width > 320 || r.width < 24 || r.height < 24) return false;
            if (text.length > 40) return false;
            var imgs = el.querySelectorAll('img');
            for (var k = 0; k < imgs.length; k++) {
              var ir = imgs[k].getBoundingClientRect();
              if (ir.width >= 36 && ir.height >= 36) return true;
            }
            return false;
          }

          function hide(el) {
            if (!el || el === document.body || el === document.documentElement) return;
            if (el.getAttribute('data-dw-hide') === '1') return;
            el.setAttribute('data-dw-hide', '1');
            el.style.setProperty('display', 'none', 'important');
          }

          function sweep() {
            var article = findArticle();
            var nodes = document.querySelectorAll('div,section,aside,nav,header,footer,iframe,ins');
            var hidCover = false;
            var vw = window.innerWidth || 1;
            var vh = window.innerHeight || 1;
            for (var i = 0; i < nodes.length; i++) {
              var el = nodes[i];
              if (!el || el === document.body || el.getAttribute('data-dw-hide') === '1') continue;
              if (article && (el === article || el.contains(article))) continue;
              var role = el.getAttribute('role');
              if (isAdEl(el) || role === 'dialog' || role === 'alertdialog') {
                hide(el);
                continue;
              }
              var s;
              try { s = getComputedStyle(el); } catch (e) { continue; }
              if (!s || (s.position !== 'fixed' && s.position !== 'sticky')) continue;
              if (s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity) === 0) continue;
              if (article && article.contains(el) && s.position === 'sticky') continue;
              var r = el.getBoundingClientRect();
              if (r.width < 8 || r.height < 8) continue;
              var covers = r.width >= vw * 0.5 && r.height >= vh * 0.3;
              var bar = r.height <= 160 && r.width >= vw * 0.55 && (r.top <= 8 || r.bottom >= vh - 8);
              var side = r.width <= Math.min(460, vw * 0.45) && (r.left <= 16 || r.right >= vw - 16);
              var corner = r.width <= 260 && r.height <= 260;
              var zi = parseInt(s.zIndex, 10);
              if (isNaN(zi)) zi = 0;
              var text = '';
              try { text = (el.innerText || '').trim(); } catch (e2) { text = ''; }
              var popup = s.position === 'fixed' && zi >= 20 && r.width <= vw * 0.92 && r.height <= vh * 0.85 && text.length < 500;
              if (!(covers || bar || side || corner || popup || isPromoFloat(el, s, r, text))) continue;
              if (covers && el.querySelectorAll('p').length >= 4) continue;
              if (!covers && text.length > 700) continue;
              if (side && !covers && !bar) {
                var links = el.querySelectorAll('a').length;
                var media = el.querySelectorAll('img,iframe,video').length;
                if (links >= 6 && media === 0 && text.length > 40) continue;
              }
              hide(el);
              if (covers) hidCover = true;
            }
            if (hidCover && document.body) {
              var bs = getComputedStyle(document.body);
              if (bs.overflow === 'hidden' || bs.overflowY === 'hidden' || bs.position === 'fixed') {
                document.body.style.setProperty('overflow', 'auto', 'important');
                document.body.style.setProperty('position', 'static', 'important');
                document.documentElement.style.setProperty('overflow', 'auto', 'important');
              }
            }
          }

          window.__dwSweep = function () { try { sweep(); } catch (e) {} };

          function kick() { window.__dwSweep(); }
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', kick, { once: true });
          else kick();
          window.addEventListener('load', kick, { once: true });

          var pending = 0;
          var mo = new MutationObserver(function () {
            if (pending) return;
            pending = setTimeout(function () { pending = 0; kick(); }, 400);
          });
          function arm() {
            if (!document.body) return;
            try { mo.observe(document.body, { childList: true, subtree: true }); } catch (e) {}
            kick();
          }
          if (document.body) arm();
          else document.addEventListener('DOMContentLoaded', arm, { once: true });

          var ticks = 0;
          var timer = setInterval(function () {
            ticks++;
            kick();
            if (ticks === 8) {
              clearInterval(timer);
              timer = setInterval(kick, 3000);
            }
          }, 800);
        })();
        """;
}
