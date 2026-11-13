export default function (view) {
    'use strict';

    var pluginId = 'a1b2c3d4-5678-9abc-def0-123456789abc';

    var STRINGS = {
        'summary.total': {
            zh: '.strm 媒体数量',
            zhHant: '.strm 媒體數量',
            en: '.strm items'
        },
        'summary.probed': {
            zh: '已有流信息',
            zhHant: '已有流資訊',
            en: 'With stream info'
        },
        'summary.unprobed': {
            zh: '缺少流信息',
            zhHant: '缺少流資訊',
            en: 'Missing stream info'
        },
        'summary.sizeMissing': {
            zh: '缺少文件大小',
            zhHant: '缺少檔案大小',
            en: 'Missing file size'
        },
        'probe.mode.incremental': {
            zh: '增量探测',
            zhHant: '增量探測',
            en: 'Incremental'
        },
        'probe.mode.full': {
            zh: '全量探测',
            zhHant: '全量探測',
            en: 'Full'
        },
        'probe.start': {
            zh: '启动预探测',
            zhHant: '啟動預探測',
            en: 'Start pre-probe'
        },
        'probe.stop': {
            zh: '停止',
            zhHant: '停止',
            en: 'Stop'
        },
        'probe.currentItemLabel': {
            zh: '正在探测: ',
            zhHant: '正在探測: ',
            en: 'Probing: '
        },
        'status.prefix': {
            zh: '执行状态: ',
            zhHant: '執行狀態: ',
            en: 'Status: '
        },
        'status.state.Idle': {
            zh: '空闲',
            zhHant: '閒置',
            en: 'Idle'
        },
        'status.state.Running': {
            zh: '运行中',
            zhHant: '執行中',
            en: 'Running'
        },
        'status.state.Stopping': {
            zh: '停止中',
            zhHant: '停止中',
            en: 'Stopping'
        },
        'config.concurrency': {
            zh: '探测并发数',
            zhHant: '探測併發數',
            en: 'Probe concurrency'
        },
        'config.autoProbe': {
            zh: 'Strm入库自动探测',
            zhHant: 'Strm 入庫自動探測',
            en: 'Auto-probe STRM items on import'
        },
        'config.remoteSize': {
            zh: '补全远端文件大小',
            zhHant: '補齊遠端檔案大小',
            en: 'Fill in the remote file size'
        },
        'config.remoteSizeAlwaysOn': {
            zh: '（始终启用）',
            zhHant: '（始終啟用）',
            en: '(always on)'
        },
        'config.remoteSizeTimeout': {
            zh: '大小请求超时（秒）',
            zhHant: '大小請求逾時（秒）',
            en: 'Size request timeout (seconds)'
        },
        'config.allowPrivateNetworkTargets': {
            zh: '允许请求内网地址',
            zhHant: '允許請求內網位址',
            en: 'Allow requests to private network addresses'
        },
        'config.skipUnprobed': {
            zh: '未探测的 STRM 文件也跳过探测',
            zhHant: '未探測的 STRM 檔案也跳過探測',
            en: 'Also skip probing for unprobed STRM files'
        },
        'config.afterStartMode': {
            zh: '起播后策略',
            zhHant: '起播後策略',
            en: 'After start'
        },
        'config.afterStart.none': {
            zh: '不补探测（默认）',
            zhHant: '不補探測（預設）',
            en: 'No re-probe (default)'
        },
        'config.afterStart.background': {
            zh: '后台补探测',
            zhHant: '背景補探測',
            en: 'Re-probe in background'
        },
        'config.backgroundDelay': {
            zh: '补探测延迟（秒）',
            zhHant: '補探測延遲（秒）',
            en: 'Re-probe delay (seconds)'
        },
        'config.cancelOnStop': {
            zh: '播放停止后取消补探测',
            zhHant: '播放停止後取消補探測',
            en: 'Cancel re-probe when playback stops'
        },
        'userPolicy.title': {
            zh: '按用户播放策略',
            zhHant: '依使用者播放策略',
            en: 'Per-user playback policy'
        },
        'userPolicy.empty': {
            zh: '暂无用户级配置，所有用户继承全局策略。',
            zhHant: '尚無使用者層級設定，所有使用者皆沿用全域策略。',
            en: 'No per-user settings yet. All users inherit the global policy.'
        },
        'userPolicy.user': {
            zh: '用户',
            zhHant: '使用者',
            en: 'User'
        },
        'userPolicy.mode': {
            zh: '播放策略',
            zhHant: '播放策略',
            en: 'Playback policy'
        },
        'userPolicy.add': {
            zh: '添加',
            zhHant: '新增',
            en: 'Add'
        },
        'userPolicy.deletedUser': {
            zh: '已删除的用户 ({0})',
            zhHant: '已刪除的使用者 ({0})',
            en: 'Deleted user ({0})'
        },
        'userPolicy.summary': {
            zh: '已配置 {0} 个用户，{1} 个用户继承全局',
            zhHant: '已設定 {0} 位使用者，{1} 位沿用全域',
            en: '{0} configured, {1} inheriting the global policy'
        },
        'userPolicy.inheritGlobal': {
            zh: '继承全局 ({0}s)',
            zhHant: '沿用全域 ({0}s)',
            en: 'Inherit global ({0}s)'
        },
        'userPolicy.remove': {
            zh: '移除',
            zhHant: '移除',
            en: 'Remove'
        },
        'userPolicy.allConfigured': {
            zh: '所有用户均已配置',
            zhHant: '所有使用者皆已設定',
            en: 'All users are already configured'
        },
        'userPolicy.noUsers': {
            zh: '无可用用户',
            zhHant: '無可用使用者',
            en: 'No users available'
        },
        'userPolicy.usersLoadFailed': {
            zh: '无法读取用户列表，现有用户策略保持不变',
            zhHant: '無法讀取使用者清單，現有使用者策略維持不變',
            en: 'Unable to load the user list. Existing per-user policies are unchanged.'
        },
        'policyMode.Inherit': {
            zh: '继承全局',
            zhHant: '沿用全域',
            en: 'Inherit global'
        },
        'policyMode.InstantNoProbe': {
            zh: '跳过探测 · 不补探测',
            zhHant: '跳過探測 · 不補探測',
            en: 'Skip probe · no re-probe'
        },
        'policyMode.InstantBackgroundProbe': {
            zh: '跳过探测 · 后台补探测',
            zhHant: '跳過探測 · 背景補探測',
            en: 'Skip probe · re-probe in background'
        },
        'policyMode.OfficialProbe': {
            zh: '不跳过探测（官方 ffprobe）',
            zhHant: '不跳過探測（官方 ffprobe）',
            en: 'Do not skip probe (official ffprobe)'
        },
        'common.save': {
            zh: '保存',
            zhHant: '儲存',
            en: 'Save'
        }
    };

    var LANG_HANS = 'zh';
    var LANG_HANT = 'zhHant';
    var LANG_EN = 'en';

    function detectLanguage() {
        var signals = [];

        var docLang = document.documentElement.getAttribute('lang');
        if (docLang) {
            signals.push(docLang);
        }

        try {
            if (window.Globalize && window.Globalize.culture) {
                var cultureName = window.Globalize.culture().name;
                if (cultureName) {
                    signals.push(cultureName);
                }
            }
        } catch (e) {
        }

        if (!signals.length) {
            try {
                if (navigator.language) {
                    signals.push(navigator.language);
                }
            } catch (e2) {
            }
        }

        var matched = null;
        for (var i = 0; i < signals.length; i++) {
            var code = String(signals[i]).toLowerCase().replace(/_/g, '-');
            if (code.indexOf('zh') !== 0 && code.indexOf('chinese') === -1) {
                continue;
            }

            var isHant = code.indexOf('hant') !== -1
                || code.indexOf('-tw') !== -1
                || code.indexOf('-hk') !== -1
                || code.indexOf('-mo') !== -1;
            if (isHant) {
                return LANG_HANT;
            }
            matched = LANG_HANS;
        }

        return matched || LANG_EN;
    }

    var currentLanguage = detectLanguage();

    function t(key) {
        var entry = STRINGS[key];
        var text = entry ? (entry[currentLanguage] || entry.zh) : null;
        if (text === null || text === undefined) {
            text = key;
        }

        for (var i = 1; i < arguments.length; i++) {
            text = text.replace('{' + (i - 1) + '}', arguments[i]);
        }
        return text;
    }

    function applyTranslations() {
        var nodes = view.querySelectorAll('[data-i18n]');
        for (var i = 0; i < nodes.length; i++) {
            var node = nodes[i];
            var entry = STRINGS[node.getAttribute('data-i18n')];
            if (!entry) {
                continue;
            }
            node.textContent = entry[currentLanguage] || entry.zh;
        }
    }

    applyTranslations();

    var lblTotal = view.querySelector('#lblSummaryTotal');
    var lblProbed = view.querySelector('#lblSummaryProbed');
    var lblUnprobed = view.querySelector('#lblSummaryUnprobed');
    var lblSizeMissing = view.querySelector('#lblSummarySizeMissing');
    var selectMode = view.querySelector('#selectProbeMode');
    var btnStart = view.querySelector('#btnStartProbe');
    var btnStop = view.querySelector('#btnStopProbe');
    var lblTaskState = view.querySelector('#lblTaskState');
    var progressBar = view.querySelector('#probeProgressBar');
    var lblCurrentItem = view.querySelector('#lblCurrentItem');
    var chkAutoProbe = view.querySelector('#chkEnableAutoProbe');
    var txtProbeConcurrency = view.querySelector('#txtProbeConcurrency');
    var txtRemoteSizeTimeout = view.querySelector('#txtRemoteSizeTimeout');
    var chkAllowPrivateNetworkTargets = view.querySelector('#chkAllowPrivateNetworkTargets');
    var chkSkipProbeForUnprobed = view.querySelector('#chkSkipProbeForUnprobedStrm');
    var selectUnprobedPlaybackMode = view.querySelector('#selectUnprobedPlaybackMode');
    var txtBackgroundProbeDelay = view.querySelector('#txtBackgroundProbeDelay');
    var backgroundProbeDelayContainer = view.querySelector('#backgroundProbeDelayContainer');
    var chkCancelProbeOnPlaybackStop = view.querySelector('#chkCancelProbeOnPlaybackStop');
    var cancelProbeOnStopContainer = view.querySelector('#cancelProbeOnStopContainer');
    var form = view.querySelector('.fastStrmConfigurationForm');

    var userPolicyRows = view.querySelector('#userPolicyRows');
    var userPolicyEmpty = view.querySelector('#userPolicyEmpty');
    var lblUserPolicySummary = view.querySelector('#lblUserPolicySummary');
    var selectAddUser = view.querySelector('#selectAddUserPolicyUser');
    var selectAddMode = view.querySelector('#selectAddUserPolicyMode');
    var txtAddDelay = view.querySelector('#txtAddUserPolicyDelay');
    var btnAddUserPolicy = view.querySelector('#btnAddUserPolicy');

    var pollTimer = null;
    var visible = false;

    var userPolicies = [];
    var users = [];
    var globalDelaySeconds = 5;

    var policyModeOrder = ['Inherit', 'InstantNoProbe', 'InstantBackgroundProbe', 'OfficialProbe'];

    var policyModeLabels = {};
    policyModeOrder.forEach(function (mode) {
        policyModeLabels[mode] = t('policyMode.' + mode);
    });

    function apiGet(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'GET', dataType: 'json' });
    }

    function apiPost(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'POST' });
    }

    function clampDelaySeconds(value) {
        var seconds = parseInt(value, 10);
        if (isNaN(seconds)) {
            return 5;
        }
        return Math.min(3600, Math.max(0, seconds));
    }

    function clampConcurrency(value) {
        var limit = parseInt(value, 10);
        if (isNaN(limit)) {
            return 1;
        }
        return Math.min(16, Math.max(1, limit));
    }

    function clampRemoteSizeTimeout(value) {
        var seconds = parseInt(value, 10);
        if (isNaN(seconds)) {
            return 10;
        }
        return Math.min(60, Math.max(1, seconds));
    }

    function policyKey(userId) {
        return String(userId).toLowerCase();
    }

    function updateUnprobedPlaybackState() {
        var instantPlayback = chkSkipProbeForUnprobed.checked;
        var backgroundProbe = instantPlayback && selectUnprobedPlaybackMode.value === 'Background';

        selectUnprobedPlaybackMode.disabled = !instantPlayback;
        txtBackgroundProbeDelay.disabled = !backgroundProbe;
        chkCancelProbeOnPlaybackStop.disabled = !backgroundProbe;
        backgroundProbeDelayContainer.style.opacity = backgroundProbe ? '1' : '0.5';
        cancelProbeOnStopContainer.style.opacity = backgroundProbe ? '1' : '0.5';
    }

    function findUserName(userId) {
        var key = policyKey(userId);
        for (var i = 0; i < users.length; i++) {
            if (policyKey(users[i].Id) === key) {
                return users[i].Name;
            }
        }

        return users.length
            ? t('userPolicy.deletedUser', String(userId).substring(0, 8))
            : String(userId).substring(0, 8);
    }

    function updatePolicySummary() {
        var configured = 0;
        for (var i = 0; i < userPolicies.length; i++) {
            if (userPolicies[i].Mode !== 'Inherit') {
                configured++;
            }
        }

        var inherited = Math.max(0, users.length - configured);
        lblUserPolicySummary.textContent = t('userPolicy.summary', configured, inherited);
    }

    function syncRowState(policy, modeSelect, delayInput) {
        var background = policy.Mode === 'InstantBackgroundProbe';
        delayInput.disabled = !background;
        delayInput.style.opacity = background ? '1' : '0.45';
        modeSelect.title = policyModeLabels[policy.Mode] || '';

        if (!background) {
            delayInput.value = '';
        }
    }

    function createPolicyRow(policy) {
        var row = document.createElement('div');
        row.style.cssText = 'display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1.6fr) minmax(0, 0.9fr) auto;'
            + ' gap: 0.6em; align-items: center;';

        var nameCell = document.createElement('div');
        nameCell.textContent = findUserName(policy.UserId);
        nameCell.title = String(policy.UserId);
        nameCell.style.cssText = 'font-size: 0.9em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;';

        var modeSelect = document.createElement('select');
        modeSelect.className = 'emby-select fsrm-control fsrm-control-sm';
        modeSelect.style.cssText = 'margin: 0;';
        policyModeOrder.forEach(function (mode) {
            var option = document.createElement('option');
            option.value = mode;
            option.textContent = policyModeLabels[mode];
            modeSelect.appendChild(option);
        });
        modeSelect.value = policyModeLabels[policy.Mode] ? policy.Mode : 'Inherit';

        var delayInput = document.createElement('input');
        delayInput.type = 'number';
        delayInput.min = '0';
        delayInput.max = '3600';
        delayInput.step = '1';
        delayInput.className = 'emby-input fsrm-control fsrm-control-sm';
        delayInput.style.cssText = 'margin: 0;';
        delayInput.placeholder = t('userPolicy.inheritGlobal', globalDelaySeconds);
        delayInput.value = policy.BackgroundProbeDelaySeconds === null
            || policy.BackgroundProbeDelaySeconds === undefined
            ? ''
            : String(policy.BackgroundProbeDelaySeconds);

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised';
        removeBtn.style.cssText = 'margin: 0; min-height: 36px; min-width: 72px;';
        removeBtn.textContent = t('userPolicy.remove');

        modeSelect.addEventListener('change', function () {
            policy.Mode = modeSelect.value;
            if (policy.Mode !== 'InstantBackgroundProbe') {
                policy.BackgroundProbeDelaySeconds = null;
            }
            syncRowState(policy, modeSelect, delayInput);
            updatePolicySummary();
        });

        delayInput.addEventListener('change', function () {
            if (delayInput.value === '') {
                policy.BackgroundProbeDelaySeconds = null;
                return;
            }

            policy.BackgroundProbeDelaySeconds = clampDelaySeconds(delayInput.value);
            delayInput.value = String(policy.BackgroundProbeDelaySeconds);
        });

        removeBtn.addEventListener('click', function () {
            userPolicies = userPolicies.filter(function (item) {
                return policyKey(item.UserId) !== policyKey(policy.UserId);
            });
            renderUserPolicies();
        });

        syncRowState(policy, modeSelect, delayInput);

        row.appendChild(nameCell);
        row.appendChild(modeSelect);
        row.appendChild(delayInput);
        row.appendChild(removeBtn);
        return row;
    }

    function renderAddUserOptions() {
        var configured = {};
        userPolicies.forEach(function (policy) {
            configured[policyKey(policy.UserId)] = true;
        });

        var available = users.filter(function (user) {
            return !configured[policyKey(user.Id)];
        });

        selectAddUser.innerHTML = '';
        if (!available.length) {
            var placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = users.length ? t('userPolicy.allConfigured') : t('userPolicy.noUsers');
            selectAddUser.appendChild(placeholder);
            selectAddUser.disabled = true;
            btnAddUserPolicy.disabled = true;
            return;
        }

        available.forEach(function (user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            selectAddUser.appendChild(option);
        });

        selectAddUser.disabled = false;
        btnAddUserPolicy.disabled = false;
    }

    function renderUserPolicies() {
        userPolicyRows.innerHTML = '';
        userPolicyEmpty.style.display = userPolicies.length ? 'none' : 'block';

        userPolicies.forEach(function (policy) {
            userPolicyRows.appendChild(createPolicyRow(policy));
        });

        renderAddUserOptions();
        updatePolicySummary();
    }

    function collectUserPolicies() {
        var result = [];
        userPolicies.forEach(function (policy) {
            if (!policy.Mode || policy.Mode === 'Inherit') {
                return;
            }

            result.push({
                UserId: policy.UserId,
                Mode: policy.Mode,
                BackgroundProbeDelaySeconds: policy.Mode === 'InstantBackgroundProbe'
                    && policy.BackgroundProbeDelaySeconds !== null
                    && policy.BackgroundProbeDelaySeconds !== undefined
                    ? clampDelaySeconds(policy.BackgroundProbeDelaySeconds)
                    : null
            });
        });
        return result;
    }

    function normalizePolicies(raw) {
        if (!raw || !raw.length) {
            return [];
        }

        var seen = {};
        var result = [];
        for (var i = 0; i < raw.length; i++) {
            var item = raw[i];
            if (!item || !item.UserId) {
                continue;
            }

            var key = policyKey(item.UserId);
            if (seen[key]) {
                continue;
            }
            seen[key] = true;

            result.push({
                UserId: item.UserId,
                Mode: policyModeLabels[item.Mode] ? item.Mode : 'Inherit',
                BackgroundProbeDelaySeconds: item.BackgroundProbeDelaySeconds === null
                    || item.BackgroundProbeDelaySeconds === undefined
                    ? null
                    : item.BackgroundProbeDelaySeconds
            });
        }
        return result;
    }

    function loadUsers() {
        return ApiClient.getUsers().then(function (list) {
            users = (list || []).map(function (user) {
                return { Id: user.Id, Name: user.Name || String(user.Id) };
            });
            renderUserPolicies();
        }).catch(function () {
            lblUserPolicySummary.textContent = t('userPolicy.usersLoadFailed');
            btnAddUserPolicy.disabled = true;
            selectAddUser.disabled = true;
        });
    }

    function loadSummary(forceRefresh) {
        return apiGet('FastStrm/Summary' + (forceRefresh ? '?refresh=true' : '')).then(function (data) {
            lblTotal.textContent = data.Total;
            lblProbed.textContent = data.Probed;
            lblUnprobed.textContent = data.Unprobed;
            lblSizeMissing.textContent = data.SizeMissing === undefined || data.SizeMissing === null
                ? '-'
                : data.SizeMissing;
        }).catch(function () {
        });
    }

    function loadStatus() {
        return apiGet('FastStrm/Probe/Status').then(function (data) {
            var stateText = STRINGS['status.state.' + data.State] ? t('status.state.' + data.State) : data.State;
            lblTaskState.textContent = stateText;
            lblCurrentItem.textContent = data.CurrentItem || '-';

            var pct = data.Total > 0 ? Math.round((data.Processed / data.Total) * 100) : 0;
            progressBar.style.width = pct + '%';

            if (data.State === 'Running' || data.State === 'Stopping') {
                btnStart.style.display = 'none';
                btnStop.style.display = 'inline-flex';
                scheduleNextPoll();
            } else {
                btnStart.style.display = 'inline-flex';
                btnStop.style.display = 'none';
                stopPolling();
                loadSummary(true);
            }
        });
    }

    function loadConfig() {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            chkAutoProbe.checked = config.EnableAutoProbe;
            txtProbeConcurrency.value = clampConcurrency(config.MaxConcurrentProbes);
            txtRemoteSizeTimeout.value = clampRemoteSizeTimeout(config.RemoteSizeTimeoutSeconds);
            if (chkAllowPrivateNetworkTargets) {
                chkAllowPrivateNetworkTargets.checked = config.AllowPrivateNetworkTargets !== false;
            }
            chkSkipProbeForUnprobed.checked = config.SkipProbeForUnprobedStrm !== false;
            selectUnprobedPlaybackMode.value = config.EnableBackgroundProbeForUnprobedStrm === true
                ? 'Background'
                : 'None';
            txtBackgroundProbeDelay.value = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            chkCancelProbeOnPlaybackStop.checked = config.CancelBackgroundProbeOnPlaybackStop !== false;
            updateUnprobedPlaybackState();

            globalDelaySeconds = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            userPolicies = normalizePolicies(config.UserPolicies);
            renderUserPolicies();
        });
    }

    function scheduleNextPoll() {
        if (!visible || pollTimer) {
            return;
        }
        pollTimer = setTimeout(pollStatus, 1500);
    }

    function pollStatus() {
        pollTimer = null;
        if (!visible) {
            return;
        }

        loadStatus().catch(function () {
            scheduleNextPoll();
        });
    }

    function stopPolling() {
        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    btnStart.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Start?mode=' + encodeURIComponent(selectMode.value)).then(function () {
            loadStatus();
        }).catch(function () {
        });
    });

    btnStop.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Stop').then(function () {
            loadStatus();
        }).catch(function () {
        });
    });

    btnAddUserPolicy.addEventListener('click', function () {
        var userId = selectAddUser.value;
        if (!userId) {
            return;
        }

        var mode = selectAddMode.value;
        var delay = txtAddDelay.value === '' ? null : clampDelaySeconds(txtAddDelay.value);

        userPolicies = userPolicies.filter(function (policy) {
            return policyKey(policy.UserId) !== policyKey(userId);
        });
        userPolicies.push({
            UserId: userId,
            Mode: mode,
            BackgroundProbeDelaySeconds: mode === 'InstantBackgroundProbe' ? delay : null
        });

        txtAddDelay.value = '';
        renderUserPolicies();
    });

    function updateAddRowState() {
        var background = selectAddMode.value === 'InstantBackgroundProbe';
        txtAddDelay.disabled = !background;
        txtAddDelay.style.opacity = background ? '1' : '0.45';
        txtAddDelay.placeholder = t('userPolicy.inheritGlobal', globalDelaySeconds);
        if (!background) {
            txtAddDelay.value = '';
        }
    }

    selectAddMode.addEventListener('change', updateAddRowState);

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EnableAutoProbe = chkAutoProbe.checked;
            config.SkipProbeForUnprobedStrm = chkSkipProbeForUnprobed.checked;
            config.EnableBackgroundProbeForUnprobedStrm = selectUnprobedPlaybackMode.value === 'Background';
            config.BackgroundProbeDelaySeconds = clampDelaySeconds(txtBackgroundProbeDelay.value);
            config.CancelBackgroundProbeOnPlaybackStop = chkCancelProbeOnPlaybackStop.checked;
            config.MaxConcurrentProbes = clampConcurrency(txtProbeConcurrency.value);
            config.RemoteSizeTimeoutSeconds = clampRemoteSizeTimeout(txtRemoteSizeTimeout.value);
            if (chkAllowPrivateNetworkTargets) {
                config.AllowPrivateNetworkTargets = chkAllowPrivateNetworkTargets.checked;
            }
            config.UserPolicies = collectUserPolicies();
            txtBackgroundProbeDelay.value = config.BackgroundProbeDelaySeconds;
            txtProbeConcurrency.value = config.MaxConcurrentProbes;
            txtRemoteSizeTimeout.value = config.RemoteSizeTimeoutSeconds;
            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();

                globalDelaySeconds = config.BackgroundProbeDelaySeconds;
                userPolicies = normalizePolicies(config.UserPolicies);
                renderUserPolicies();
                updateAddRowState();
            });
        });
    });

    chkSkipProbeForUnprobed.addEventListener('change', updateUnprobedPlaybackState);
    selectUnprobedPlaybackMode.addEventListener('change', updateUnprobedPlaybackState);

    view.addEventListener('viewshow', function () {
        visible = true;
        applyTranslations();
        loadSummary();
        loadStatus().catch(function () {
        });
        loadConfig().then(loadUsers).catch(function () {
        });
        updateUnprobedPlaybackState();
        updateAddRowState();
    });

    view.addEventListener('viewhide', function () {
        visible = false;
        stopPolling();
    });
}
