---
permalink: /assets/site.js
---
// Progressive enhancement only: the site reads fine without any of this. No dependencies,
// nothing leaves the browser except the theme preference, which stays in localStorage.
(function () {
  'use strict';

  var root = document.documentElement;

  function on(element, event, handler) {
    if (element) element.addEventListener(event, handler);
  }

  // Theme toggle: system preference by default, an explicit choice remembered per browser.
  on(document.querySelector('.theme-toggle'), 'click', function () {
    var explicit = root.getAttribute('data-theme');
    var dark = explicit ? explicit === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
    var next = dark ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('aesx-theme', next); } catch (e) { /* private mode, blocked storage */ }
  });

  // Site menu on narrow screens.
  var navToggle = document.querySelector('.nav-toggle');
  var siteNav = document.getElementById('site-nav');
  on(navToggle, 'click', function () {
    var open = siteNav.classList.toggle('open');
    navToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  // Documentation sidebar: a collapsible panel on narrow screens, always open on wide ones.
  var docsNav = document.getElementById('docs-nav');
  if (docsNav) {
    var wide = window.matchMedia('(min-width: 900px)');
    var syncNav = function () { docsNav.open = wide.matches; };
    syncNav();
    if (wide.addEventListener) wide.addEventListener('change', syncNav);
    else if (wide.addListener) wide.addListener(syncNav);
  }

  // "On this page": the second-level headings of the article, with the current one highlighted.
  var toc = document.getElementById('toc');
  var article = document.getElementById('content');
  if (toc && article) {
    var headings = Array.prototype.slice.call(article.querySelectorAll('h2[id]'));
    if (headings.length > 1) {
      var links = {};
      headings.forEach(function (heading) {
        var item = document.createElement('li');
        var link = document.createElement('a');
        link.href = '#' + heading.id;
        link.textContent = heading.textContent;
        item.appendChild(link);
        toc.appendChild(item);
        links[heading.id] = link;
      });
      toc.parentNode.classList.add('has-items');

      if ('IntersectionObserver' in window) {
        var current = null;
        var observer = new IntersectionObserver(function (entries) {
          entries.forEach(function (entry) {
            if (!entry.isIntersecting) return;
            if (current) current.classList.remove('active');
            current = links[entry.target.id];
            if (current) current.classList.add('active');
          });
        }, { rootMargin: '-72px 0px -70% 0px' });
        headings.forEach(function (heading) { observer.observe(heading); });
      }
    }
  }

  // Copy buttons on code blocks.
  if (navigator.clipboard && window.isSecureContext) {
    var blocks = document.querySelectorAll('.prose pre, pre.code');
    Array.prototype.forEach.call(blocks, function (pre) {
      var code = pre.querySelector('code') || pre;
      var text = code.innerText.replace(/\n$/, '');
      if (!text) return;
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'copy';
      button.textContent = 'Copy';
      button.setAttribute('aria-label', 'Copy to clipboard');
      button.addEventListener('click', function () {
        navigator.clipboard.writeText(text).then(function () {
          button.textContent = 'Copied';
          button.classList.add('done');
          setTimeout(function () {
            button.textContent = 'Copy';
            button.classList.remove('done');
          }, 1500);
        }, function () {
          button.textContent = 'Failed';
        });
      });
      pre.appendChild(button);
    });
  }
})();
