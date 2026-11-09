export default function (view) {
    'use strict';

    var pluginId = 'a1b2c3d4-5678-9abc-def0-123456789abc';

    // ── i18n ──
    // 语言跟随 Jellyfin 界面语言：识别到 zh 即中文（Hant / TW / HK / MO 走繁体），其余一律英文。
    // HTML 上的文案以简中写在标签内做兜底，脚本失效时页面依然可读；此处只做覆盖。
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
        'probe.mode.sizeOnly': {
            zh: '仅补全文件大小',
            zhHant: '僅補齊檔案大小',
            en: 'File size only'
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
        'config.concurrencyDesc': {
            zh: '增量探测、全量探测、入库自动探测与补探测统一使用该并发数，默认 1，范围 1–16。',
            zhHant: '增量探測、全量探測、入庫自動探測與補探測皆使用此併發數，預設 1，範圍 1–16。',
            en: 'Shared by incremental probing, full probing, auto-probe on import and re-probe. Default 1, range 1-16.'
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
        'config.remoteSizeDesc': {
            zh: 'Jellyfin 探测 .strm 时只能拿到文本文件本身的大小，开启后会向流地址请求真实大小并写入条目。仅在「跳过探测 · 后台补探测」策略、批量探测或「仅补全文件大小」时生效，「官方探测」与「不补探测」下不会运行。',
            zhHant: 'Jellyfin 探測 .strm 時只能取得文字檔本身的大小，開啟後會向串流位址請求真實大小並寫入項目。僅在「跳過探測 · 背景補探測」策略、批次探測或「僅補齊檔案大小」時生效，「官方探測」與「不補探測」下不會執行。',
            en: 'Jellyfin only sees the size of the .strm text file. When on, the plugin asks the stream endpoint for the real size and stores it on the item. It only runs under the "skip probe + background probe" policy, a batch probe, or the size-only mode; it never runs with "official probe" or "skip probe, no background probe".'
        },
        'config.remoteSizeTimeout': {
            zh: '大小请求超时（秒）',
            zhHant: '大小請求逾時（秒）',
            en: 'Size request timeout (seconds)'
        },
        'config.remoteSizeTimeoutDesc': {
            zh: '默认 10 秒，范围 1–60。先发 HEAD 请求，失败再退回 1 字节的 Range 请求。',
            zhHant: '預設 10 秒，範圍 1–60。先送 HEAD 請求，失敗再改用 1 位元組的 Range 請求。',
            en: 'Default 10 seconds, range 1-60. Sends a HEAD request first and falls back to a one-byte ranged GET.'
        },
        'config.allowPrivateNetworkTargets': {
            zh: '允许请求内网地址',
            zhHant: '允許請求內網位址',
            en: 'Allow requests to private network addresses'
        },
        'config.allowPrivateNetworkTargetsDesc': {
            zh: '默认开启（局域网 Emby / Alist / OpenList 需要）。关闭后只允许请求公网地址；链路本地地址（含 169.254.169.254 云元数据端点）无论如何都会被拒绝。',
            zhHant: '預設開啟（區域網路 Emby / Alist / OpenList 需要）。關閉後只允許請求公網位址；連結本地位址（含 169.254.169.254 雲端中繼資料端點）無論如何都會被拒絕。',
            en: 'On by default (a LAN Emby / Alist / OpenList origin needs it). When off, only public addresses are requested; link-local addresses, including the 169.254.169.254 cloud metadata endpoint, are always refused.'
        },
        'config.skipUnprobed': {
            zh: '未探测的 STRM 文件也跳过探测',
            zhHant: '未探測的 STRM 檔案也跳過探測',
            en: 'Also skip probing for unprobed STRM files'
        },
        'config.skipUnprobedDesc': {
            zh: '开启：跳过 ffprobe 直接起播；关闭：回退官方探测。',
            zhHant: '開啟：跳過 ffprobe 直接起播；關閉：回退官方探測。',
            en: 'On: skip ffprobe and start playback immediately. Off: fall back to official probing.'
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
        'config.afterStartDesc': {
            zh: '仅在开关开启时生效；不补探测不会再次请求流地址。',
            zhHant: '僅在開關開啟時生效；不補探測不會再次請求串流位址。',
            en: 'Only applies while the switch above is on. No re-probe means the stream URL is never requested again.'
        },
        'config.backgroundDelay': {
            zh: '补探测延迟（秒）',
            zhHant: '補探測延遲（秒）',
            en: 'Re-probe delay (seconds)'
        },
        'config.backgroundDelayDesc': {
            zh: '默认 5 秒，范围 0–3600。',
            zhHant: '預設 5 秒，範圍 0–3600。',
            en: 'Default 5 seconds, range 0-3600.'
        },
        'config.cancelOnStop': {
            zh: '播放停止后取消补探测',
            zhHant: '播放停止後取消補探測',
            en: 'Cancel re-probe when playback stops'
        },
        'config.cancelOnStopDesc': {
            zh: '默认开启；关闭后播放停止仍会继续补探测。',
            zhHant: '預設開啟；關閉後播放停止仍會繼續補探測。',
            en: 'On by default. When off, the re-probe continues after playback stops.'
        },
        'userPolicy.title': {
            zh: '按用户播放策略',
            zhHant: '依使用者播放策略',
            en: 'Per-user playback policy'
        },
        'userPolicy.desc': {
            zh: '用户级配置优先于上方全局配置；未在此列出的用户继承全局策略。策略在每次播放请求时读取，保存后对下一次播放生效，不影响正在播放的会话。',
            zhHant: '使用者層級設定優先於上方全域設定；未在此列出的使用者沿用全域策略。策略在每次播放請求時讀取，儲存後對下一次播放生效，不影響正在播放的工作階段。',
            en: 'Per-user settings override the global settings above; users not listed here inherit the global policy. The policy is read on every playback request, so a save takes effect from the next playback and does not affect sessions already playing.'
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
        'userPolicy.footerDesc': {
            zh: '延迟留空表示沿用全局延迟。保存时「继承全局」的用户不会写入配置文件。',
            zhHant: '延遲留空表示沿用全域延遲。儲存時「沿用全域」的使用者不會寫入設定檔。',
            en: 'Leave the delay empty to use the global delay. Users set to “inherit global” are not written to the configuration file.'
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

    // 界面语言来自 Jellyfin Web：优先 <html lang>（appHost.setCulture 会写入），
    // 其次 Globalize 当前区域，两者都拿不到才退回浏览器语言。任一信号为中文即判定中文。
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
            // Globalize 可能不存在或未初始化，忽略该信号。
        }

        if (!signals.length) {
            try {
                if (navigator.language) {
                    signals.push(navigator.language);
                }
            } catch (e2) {
                // 极少数环境禁用 navigator，保持空信号 → 回落英文。
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
            // 缺词条时退回 key 本身，便于发现漏翻。
            text = key;
        }

        for (var i = 1; i < arguments.length; i++) {
            text = text.replace('{' + (i - 1) + '}', arguments[i]);
        }
        return text;
    }

    // 覆盖所有带 data-i18n 标记的节点；未覆盖的属性（placeholder/title）由各渲染函数用 t() 设置。
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

    // ── DOM references ──
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
    var chkEnableRemoteSize = view.querySelector('#chkEnableRemoteSize');
    var txtRemoteSizeTimeout = view.querySelector('#txtRemoteSizeTimeout');
    var remoteSizeTimeoutContainer = view.querySelector('#remoteSizeTimeoutContainer');
    var chkAllowPrivateNetworkTargets = view.querySelector('#chkAllowPrivateNetworkTargets');
    var allowPrivateNetworkTargetsContainer = view.querySelector('#allowPrivateNetworkTargetsContainer');
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

    // ── 按用户播放策略的编辑态 ──
    // userPolicies 是唯一可信来源：界面改动先写这里，保存时整体覆盖 config.UserPolicies。
    var userPolicies = [];
    var users = [];
    var globalDelaySeconds = 5;

    var policyModeOrder = ['Inherit', 'InstantNoProbe', 'InstantBackgroundProbe', 'OfficialProbe'];

    // key 集合固定，值随语言变化；同时用作「模式是否合法」的判定表。
    var policyModeLabels = {};
    policyModeOrder.forEach(function (mode) {
        policyModeLabels[mode] = t('policyMode.' + mode);
    });

    // ── Helpers ──

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

    // 关闭补全远端大小时，超时输入框没有意义：置灰但保留原值，避免用户以为是配置丢失。
    function updateRemoteSizeState() {
        var enabled = chkEnableRemoteSize.checked;
        txtRemoteSizeTimeout.disabled = !enabled;
        remoteSizeTimeoutContainer.style.opacity = enabled ? '1' : '0.5';
        // 流地址白名单只在会发起请求时才有意义。
        if (chkAllowPrivateNetworkTargets) {
            chkAllowPrivateNetworkTargets.disabled = !enabled;
        }
        if (allowPrivateNetworkTargetsContainer) {
            allowPrivateNetworkTargetsContainer.style.opacity = enabled ? '1' : '0.5';
        }
    }

    function policyKey(userId) {
        return String(userId).toLowerCase();
    }

    // 下拉框、延迟与停止策略只在“跳过探测”开启时可用；后两者只在“后台补探测”模式下可用。
    function updateUnprobedPlaybackState() {
        var instantPlayback = chkSkipProbeForUnprobed.checked;
        var backgroundProbe = instantPlayback && selectUnprobedPlaybackMode.value === 'Background';

        selectUnprobedPlaybackMode.disabled = !instantPlayback;
        txtBackgroundProbeDelay.disabled = !backgroundProbe;
        chkCancelProbeOnPlaybackStop.disabled = !backgroundProbe;
        backgroundProbeDelayContainer.style.opacity = backgroundProbe ? '1' : '0.5';
        cancelProbeOnStopContainer.style.opacity = backgroundProbe ? '1' : '0.5';
    }

    // ── 按用户播放策略：渲染 ──

    function findUserName(userId) {
        var key = policyKey(userId);
        for (var i = 0; i < users.length; i++) {
            if (policyKey(users[i].Id) === key) {
                return users[i].Name;
            }
        }

        // 用户列表还没加载完时只显示 Id 前缀，加载完会重新渲染出名字。
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
            // 非「后台补探测」模式不保留延迟值，避免配置里出现永远不生效的数字。
            delayInput.value = '';
        }
    }

    function createPolicyRow(policy) {
        var row = document.createElement('div');
        row.style.cssText = 'display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1.6fr) minmax(0, 0.9fr) auto;'
            + ' gap: 0.6em; align-items: center;';

        var nameCell = document.createElement('div');
        nameCell.textContent = findUserName(policy.UserId);   // textContent：用户名可能含 <>，绝不拼 HTML
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
            // 「继承全局」等于未单独配置：不写入配置文件，语义与实现保持一致。
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
                // 界面无法表达非法模式：归一为「继承全局」，保存时顺带修掉手改文件留下的坏值。
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
            // 用户列表拿不到时不能阻断全局配置的保存：既有策略仍在 userPolicies 里，原样回写。
            lblUserPolicySummary.textContent = t('userPolicy.usersLoadFailed');
            btnAddUserPolicy.disabled = true;
            selectAddUser.disabled = true;
        });
    }

    // ── Data loading ──

    function loadSummary(forceRefresh) {
        return apiGet('FastStrm/Summary' + (forceRefresh ? '?refresh=true' : '')).then(function (data) {
            lblTotal.textContent = data.Total;
            lblProbed.textContent = data.Probed;
            lblUnprobed.textContent = data.Unprobed;
            // 旧版服务端不返回该字段：显示 "-" 而不是 "undefined"。
            lblSizeMissing.textContent = data.SizeMissing === undefined || data.SizeMissing === null
                ? '-'
                : data.SizeMissing;
        }).catch(function () {
            // The summary is a whole-library scan on the server; a transient failure only means
            // the counters stay as they are, so do not surface it as an unhandled rejection.
        });
    }

    function loadStatus() {
        return apiGet('FastStrm/Probe/Status').then(function (data) {
            // 服务端未来新增的状态值不会命中词条，原样显示比显示 key 更有用。
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
                // A run just ended, so the cached counters on the server are stale.
                loadSummary(true);
            }
        });
    }

    function loadConfig() {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            chkAutoProbe.checked = config.EnableAutoProbe;
            // 默认值为 true：旧配置文件中没有该字段时也保持"跳过探测"的极速模式
            txtProbeConcurrency.value = clampConcurrency(config.MaxConcurrentProbes);
            // 默认开启：旧配置文件没有该字段时同样补全远端文件大小
            chkEnableRemoteSize.checked = config.EnableRemoteSize !== false;
            txtRemoteSizeTimeout.value = clampRemoteSizeTimeout(config.RemoteSizeTimeoutSeconds);
            // 默认允许内网目标：旧配置文件没有该字段时保持既有行为（局域网媒体服务器）
            if (chkAllowPrivateNetworkTargets) {
                chkAllowPrivateNetworkTargets.checked = config.AllowPrivateNetworkTargets !== false;
            }
            updateRemoteSizeState();
            chkSkipProbeForUnprobed.checked = config.SkipProbeForUnprobedStrm !== false;
            // 默认不补探测：旧配置文件中没有该字段时按“直接起播，不补探测”处理
            selectUnprobedPlaybackMode.value = config.EnableBackgroundProbeForUnprobedStrm === true
                ? 'Background'
                : 'None';
            txtBackgroundProbeDelay.value = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            // 默认开启：旧配置文件中没有该字段时，播放停止即取消补探测
            chkCancelProbeOnPlaybackStop.checked = config.CancelBackgroundProbeOnPlaybackStop !== false;
            updateUnprobedPlaybackState();

            // 旧配置文件没有 UserPolicies 字段，按“无用户级配置”处理
            globalDelaySeconds = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            userPolicies = normalizePolicies(config.UserPolicies);
            renderUserPolicies();
        });
    }

    // ── Polling ──

    // A self-rescheduling timeout instead of setInterval: it never stacks requests when the
    // server is slow, and `visible` prevents an in-flight response from restarting polling
    // after the page was hidden.
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
            // Keep polling after a transient request failure; a successful response decides
            // whether another round is needed.
            scheduleNextPoll();
        });
    }

    function stopPolling() {
        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    // ── Event handlers ──

    btnStart.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Start?mode=' + encodeURIComponent(selectMode.value)).then(function () {
            loadStatus();
        }).catch(function () {
            // A rejected start (409 while another run is winding down) leaves the buttons as-is;
            // the next status poll corrects them.
        });
    });

    btnStop.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Stop').then(function () {
            loadStatus();
        }).catch(function () {
            // Ignore: the status poll still reflects the real server state.
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
            config.EnableRemoteSize = chkEnableRemoteSize.checked;
            config.RemoteSizeTimeoutSeconds = clampRemoteSizeTimeout(txtRemoteSizeTimeout.value);
            if (chkAllowPrivateNetworkTargets) {
                config.AllowPrivateNetworkTargets = chkAllowPrivateNetworkTargets.checked;
            }
            config.UserPolicies = collectUserPolicies();
            txtBackgroundProbeDelay.value = config.BackgroundProbeDelaySeconds;
            txtProbeConcurrency.value = config.MaxConcurrentProbes;
            txtRemoteSizeTimeout.value = config.RemoteSizeTimeoutSeconds;
            updateRemoteSizeState();
            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();

                // 保存后按实际落盘内容回填：被归一为「继承全局」的条目会从列表消失，
                // 与配置文件保持一致，避免界面显示一份、文件里是另一份。
                globalDelaySeconds = config.BackgroundProbeDelaySeconds;
                userPolicies = normalizePolicies(config.UserPolicies);
                renderUserPolicies();
                updateAddRowState();
            });
        });
    });

    chkSkipProbeForUnprobed.addEventListener('change', updateUnprobedPlaybackState);
    selectUnprobedPlaybackMode.addEventListener('change', updateUnprobedPlaybackState);
    chkEnableRemoteSize.addEventListener('change', updateRemoteSizeState);

    // ── Page lifecycle ──

    view.addEventListener('viewshow', function () {
        visible = true;
        // 页面可能被缓存复用：语言若在外部被改过，重新套用文字（幂等）。
        applyTranslations();
        loadSummary();
        loadStatus().catch(function () {
            // Transient failure while opening the page; the next status poll/visit retries.
        });
        loadConfig().then(loadUsers).catch(function () {
            // 配置或用户列表读取失败时页面保持可用状态，保存仍会带上已有策略。
        });
        updateUnprobedPlaybackState();
        updateAddRowState();
        updateRemoteSizeState();
    });

    view.addEventListener('viewhide', function () {
        visible = false;
        stopPolling();
    });
}
