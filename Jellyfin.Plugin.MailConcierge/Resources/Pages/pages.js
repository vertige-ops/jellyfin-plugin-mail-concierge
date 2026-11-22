(function () {
    'use strict';

    var data = JSON.parse(document.getElementById('mailconcierge-data').textContent);
    var strings = data.strings;
    var base = data.base;
    var params = new URLSearchParams(window.location.search);
    // The server puts the token in the page data (it received it in a POST body); the query is the legacy link format.
    var token = data.token || params.get('t') || '';
    var session = data.session || params.get('s') || '';
    if (window.location.search) {
        // A token in the address must not stay in the history, the Referer or a copied link.
        try { window.history.replaceState(null, '', window.location.pathname); } catch (e) { /* the address stays */ }
    }
    // The page opened by the entry page is served at Open/<page> (a POST result): show the plain page address instead.
    var opened = /^(.*\/MailConcierge\/)Open\/(reset|confirm|cancel|accept)$/.exec(window.location.pathname);
    if (opened) {
        try { window.history.replaceState(null, '', opened[1] + opened[2].charAt(0).toUpperCase() + opened[2].slice(1)); } catch (e) { /* the address stays */ }
    }
    var form = document.getElementById('reset-form') || document.getElementById('action-form') || document.getElementById('accept-form');
    var message = document.getElementById('msg');
    var submit = document.getElementById('submit');
    var done = document.getElementById('done');

    function show(text) {
        message.textContent = text;
        message.hidden = false;
    }

    function post(path, body) {
        return fetch(base + path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'omit',
            body: JSON.stringify(body)
        }).then(function (response) {
            return response.json().catch(function () { return null; }).then(function (json) {
                return { ok: response.ok, json: json };
            });
        });
    }

    function errorText(result) {
        return result && result.json && result.json.message ? result.json.message : strings[data.page === 'accept' ? 'page.accept.error.invalid' : 'page.error.invalid'];
    }

    function succeed() {
        form.hidden = true;
        done.hidden = false;
        // Only the title of the success message stays, on the page and in the browser tab.
        var formTitle = document.getElementById('form-title');
        if (formTitle) { formTitle.hidden = true; }
        var successTitle = done.querySelector('h1');
        // The focused button was just hidden: move the keyboard and screen reader position to the success title.
        if (successTitle) { successTitle.setAttribute('tabindex', '-1'); successTitle.focus(); }
        var suffix = document.title.lastIndexOf(' - ');
        if (successTitle) { document.title = successTitle.textContent + (suffix >= 0 ? document.title.substring(suffix) : ''); }
    }

    function busy(on) {
        submit.disabled = on;
        if (data.page === 'accept') {
            // The picture is already being sent: it can neither be removed nor replaced meanwhile.
            document.getElementById('avatar-remove').disabled = on;
            document.getElementById('avatar').disabled = on;
        }
    }

    function run(work) {
        message.hidden = true;
        busy(true);
        work().then(function (ok) {
            if (ok) { succeed(); } else { busy(false); }
        }, function () {
            show(strings['page.error.network']);
            busy(false);
        });
    }

    if (data.page === 'entry') {
        // Hidden in the markup so that, without JavaScript, only the noscript explanation shows.
        document.getElementById('entry-loading').hidden = false;
        // The e-mailed link keeps its token in the fragment, which the browser never sends: post it to the server.
        var fragment = new URLSearchParams(window.location.hash.slice(1));
        var fields = { t: fragment.get('t'), s: fragment.get('s') };
        var targets = ['reset', 'confirm', 'cancel', 'accept'];
        if ((fields.t || fields.s) && targets.indexOf(data.target) >= 0) {
            var opener = document.createElement('form');
            opener.method = 'post';
            opener.action = base + '/MailConcierge/Open/' + data.target;
            opener.hidden = true;
            ['t', 's'].forEach(function (name) {
                if (fields[name]) {
                    var input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = name;
                    input.value = fields[name];
                    opener.appendChild(input);
                }
            });
            document.body.appendChild(opener);
            try { window.history.replaceState(null, '', window.location.pathname); } catch (e) { /* the address stays */ }
            opener.submit();
        } else {
            document.getElementById('entry-loading').hidden = true;
            document.getElementById('entry-invalid').hidden = false;
        }
        return;
    }

    if (!form) {
        return;
    }

    // Downsizes the chosen picture to 256 px at most and resolves with its JPEG as base64 (no data-URL prefix),
    // or with an empty string when there is no picture or it cannot be read: the account is created without one.
    function readAvatar(file) {
        if (!file) {
            return Promise.resolve('');
        }

        // The image is loaded from a data: URL because the page policy allows img-src 'self' data: only (no blob:).
        return new Promise(function (resolve) {
            try {
                var reader = new FileReader();
                reader.onerror = function () { resolve(''); };
                reader.onload = function () {
                    var image = new Image();
                    image.onerror = function () { resolve(''); };
                    image.onload = function () {
                        try {
                            var ratio = Math.min(1, 256 / Math.max(image.naturalWidth, image.naturalHeight));
                            var canvas = document.createElement('canvas');
                            canvas.width = Math.max(1, Math.round(image.naturalWidth * ratio));
                            canvas.height = Math.max(1, Math.round(image.naturalHeight * ratio));
                            var context = canvas.getContext('2d');
                            // JPEG has no alpha: paint white first so transparent images do not turn black.
                            context.fillStyle = '#fff';
                            context.fillRect(0, 0, canvas.width, canvas.height);
                            context.drawImage(image, 0, 0, canvas.width, canvas.height);
                            var encoded = canvas.toDataURL('image/jpeg', 0.85);
                            var comma = encoded.indexOf(',');
                            resolve(encoded.indexOf('data:image/jpeg') === 0 && comma > 0 ? encoded.substring(comma + 1) : '');
                        } catch (e) {
                            resolve('');
                        }
                    };
                    image.src = String(reader.result);
                };
                reader.readAsDataURL(file);
            } catch (e) {
                resolve('');
            }
        });
    }

    // The chosen picture is read and resized once: the preview and the submission share the same result.
    var avatarCache = { file: null, promise: null };

    function avatarFor(file) {
        if (!file) {
            return Promise.resolve('');
        }

        if (avatarCache.file !== file) {
            avatarCache = { file: file, promise: readAvatar(file) };
        }

        return avatarCache.promise;
    }

    if (data.page === 'accept') {
        var avatarInput = document.getElementById('avatar');
        var avatarPreview = document.getElementById('avatar-preview');
        var avatarChoose = document.getElementById('avatar-choose');
        var avatarRemove = document.getElementById('avatar-remove');
        var avatarError = document.getElementById('avatar-error');

        // Shows the picture (a data: URL, allowed by the page policy) or, with an empty string, the state without one.
        function showAvatar(jpeg) {
            if (jpeg) {
                avatarPreview.src = 'data:image/jpeg;base64,' + jpeg;
            } else {
                avatarPreview.removeAttribute('src');
            }

            avatarPreview.hidden = !jpeg;
            avatarRemove.hidden = !jpeg;
            avatarChoose.textContent = strings[jpeg ? 'page.accept.avatarChange' : 'page.accept.avatarChoose'];
        }

        avatarInput.addEventListener('change', function () {
            var file = avatarInput.files && avatarInput.files[0];
            avatarError.hidden = true;
            // Whatever was previewed is stale from now on: show nothing until the new picture is ready.
            showAvatar('');
            if (!file) {
                avatarCache = { file: null, promise: null };
                return;
            }

            avatarFor(file).then(function (jpeg) {
                if (avatarCache.file !== file) {
                    return; // another picture was chosen or removed meanwhile
                }

                showAvatar(jpeg);
                if (!jpeg) {
                    // Unreadable: nothing is previewed nor sent, the account is created without a picture.
                    avatarInput.value = '';
                    avatarCache = { file: null, promise: null };
                    avatarError.hidden = false;
                }
            });
        });

        avatarRemove.addEventListener('click', function () {
            avatarInput.value = '';
            avatarCache = { file: null, promise: null };
            avatarError.hidden = true;
            showAvatar('');
            // The button just disappeared: keep the keyboard on the photo field instead of losing it.
            avatarInput.focus();
        });

        var usernameField = document.getElementById('username');
        var usernameHint = document.getElementById('username-hint');
        usernameField.addEventListener('blur', function () {
            var name = usernameField.value.trim();
            if (!name) {
                usernameHint.hidden = true;
                return;
            }

            post('/MailConcierge/Accept/Check', { token: token, username: name }).then(function (result) {
                var ok = result.ok && result.json && result.json.available === true;
                usernameHint.textContent = strings[ok ? 'page.accept.available' : 'page.accept.unavailable'];
                usernameHint.hidden = false;
            }, function () { usernameHint.hidden = true; });
        });
    }

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        if (data.page === 'accept') {
            var newPassword = document.getElementById('password').value;
            if (newPassword.length < data.minPasswordLength) {
                show(strings['password.tooShort']);
                return;
            }

            if (newPassword !== document.getElementById('confirm').value) {
                show(strings['page.reset.mismatch']);
                return;
            }

            run(function () {
                return avatarFor(avatarInput.files && avatarInput.files[0]).then(function (avatar) {
                    var body = {
                        token: token,
                        username: usernameField.value.trim(),
                        email: document.getElementById('email').value.trim(),
                        password: newPassword,
                        language: data.lang
                    };
                    if (avatar) {
                        body.avatar = avatar;
                    }

                    return post('/MailConcierge/Accept', body);
                }).then(function (result) {
                    if (!result.ok) {
                        show(errorText(result));
                        return false;
                    }

                    return true;
                });
            });
        } else if (data.page === 'reset') {
            var password = document.getElementById('password').value;
            var confirm = document.getElementById('confirm').value;
            if (password.length < data.minPasswordLength) {
                show(strings['password.tooShort']);
                return;
            }

            if (password !== confirm) {
                show(strings['page.reset.mismatch']);
                return;
            }

            run(function () {
                var getSession = session
                    ? Promise.resolve({ ok: true, json: { session: session } })
                    : post('/MailConcierge/Reset/Link', { token: token });
                return getSession.then(function (linked) {
                    if (!linked.ok || !linked.json || !linked.json.session) {
                        show(errorText(linked));
                        return false;
                    }

                    session = linked.json.session;
                    return post('/MailConcierge/Reset/Complete', { session: session, password: password }).then(function (completed) {
                        if (!completed.ok) {
                            show(errorText(completed));
                            return false;
                        }

                        return true;
                    });
                });
            });
        } else {
            var path = data.page === 'confirm' ? '/MailConcierge/Confirm' : '/MailConcierge/Cancel';
            var body = { token: token };
            if (data.page === 'cancel') {
                body.password = document.getElementById('password').value;
                if (!body.password) {
                    show(strings['page.cancel.error.wrongPassword']);
                    return;
                }
            }

            run(function () {
                return post(path, body).then(function (result) {
                    if (data.page === 'cancel') {
                        document.getElementById('password').value = '';
                    }

                    if (!result.ok) {
                        show(errorText(result));
                        return false;
                    }

                    return true;
                });
            });
        }
    });
}());
