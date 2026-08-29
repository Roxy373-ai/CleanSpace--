using System.Globalization;

namespace CleanSpace.Services;

public enum LocaleCode { ZhCn, KoKr }

public sealed class Localizer
{
    public LocaleCode Locale { get; private set; } = LocaleCode.ZhCn;
    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    public void SetLocale(LocaleCode locale)
    {
        Locale = locale;
        CultureInfo.CurrentCulture = locale == LocaleCode.KoKr ? new("ko-KR") : new("zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        return Get(key, Locale);
    }

    public static string Get(string key, LocaleCode locale)
    {
        var table = locale == LocaleCode.KoKr ? Korean : Chinese;
        return table.TryGetValue(key, out var text) ? text : key;
    }

    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>
    {
        ["app.title"] = "CleanSpace（허준영 制作）",
        ["nav.dashboard"] = "磁盘概览", ["nav.space"] = "空间分析", ["nav.media"] = "照片与视频",
        ["nav.duplicates"] = "重复文件", ["nav.apps"] = "软件管理", ["nav.cleanup"] = "清理清单",
        ["nav.history"] = "历史记录", ["nav.settings"] = "设置", ["nav.about"] = "关于",
        ["scan.system"] = "仅扫描系统盘", ["scan.all"] = "扫描所有本地磁盘", ["action.refresh_drives"] = "刷新磁盘",
        ["scan.choice"] = "“仅扫描系统盘”速度更快；“扫描所有本地磁盘”会检查当前连接的固定磁盘、移动硬盘和 USB。扫描期间拔出磁盘时，会跳过无法继续读取的位置。",
        ["admin.banner_title"] = "普通模式可以继续使用",
        ["admin.banner_text"] = "当前没有管理员权限。用户文件和大多数缓存仍可扫描，但部分 Windows 目录可能无法读取。需要更完整的系统扫描时，可以重新以管理员身份启动。",
        ["admin.continue"] = "继续普通模式", ["admin.restart"] = "以管理员身份重新启动",
        ["admin.cancelled"] = "已取消管理员模式，继续普通模式", ["admin.failed"] = "无法以管理员身份重新启动",
        ["admin.standard_active"] = "当前使用普通模式；受保护目录可能无法读取",
        ["drive.refreshed"] = "已发现 {0} 个可扫描磁盘", ["drive.none"] = "没有找到可用的本地磁盘",
        ["drive.system"] = "Windows 系统盘", ["drive.fixed"] = "本地磁盘", ["drive.removable"] = "移动硬盘或 USB",
        ["action.remove_cleanup"] = "从清单移除",
        ["action.select_file"] = "请先选择一个文件", ["action.scan_first"] = "请先完成一次磁盘扫描", ["media.scan_first"] = "请先扫描磁盘，再检查照片和视频",
        ["scan.pause"] = "暂停", ["scan.resume"] = "继续", ["scan.cancel"] = "取消",
        ["action.admin"] = "管理员模式", ["action.admin_active"] = "已是管理员",
        ["status.ready"] = "准备就绪", ["status.scanning"] = "正在扫描：{0}", ["status.paused"] = "扫描已暂停（已扫描内容仍可查看）",
        ["status.done"] = "扫描完成：{0} 个文件，共 {1}", ["status.cancelled"] = "扫描已取消，已保留 {0} 个结果",
        ["status.busy"] = "当前有操作正在进行，请稍候", ["status.cancelling"] = "正在取消…",
        ["filter.failed"] = "无法读取筛选结果", ["scan.results_failed"] = "扫描完成，但无法载入安全清理候选",
        ["status.errors"] = "无法访问：{0} 个位置",
        ["title.dashboard"] = "磁盘概览", ["title.space"] = "空间分析", ["title.media"] = "照片与视频",
        ["title.duplicates"] = "重复文件", ["title.apps"] = "软件管理", ["title.cleanup"] = "清理清单",
        ["title.history"] = "历史记录", ["title.settings"] = "设置", ["title.about"] = "关于 CleanSpace",
        ["col.select"] = "选择", ["col.name"] = "名称", ["col.path"] = "完整路径", ["col.drive"] = "分区",
        ["col.size"] = "大小", ["col.modified"] = "修改日期", ["col.risk"] = "风险", ["col.reason"] = "原因",
        ["col.status"] = "状态", ["col.publisher"] = "发布者", ["col.version"] = "版本", ["col.location"] = "位置", ["col.source"] = "来源",
        ["action.locate"] = "定位文件", ["action.open"] = "打开文件", ["action.add"] = "加入清理清单", ["action.select_duplicates"] = "选择重复副本",
        ["action.check_media"] = "检查媒体", ["action.find_duplicates"] = "检测重复文件", ["action.find_similar_photos"] = "查找相似照片", ["action.refresh"] = "刷新",
        ["action.check_residuals"] = "检查卸载残留", ["action.recycle_residuals"] = "将所选文件夹移入回收站",
        ["action.uninstall"] = "启动官方卸载程序", ["action.select_safe"] = "全选安全项", ["action.select_allowed"] = "全选可选项",
        ["action.clear_selection"] = "取消全选", ["action.recycle"] = "移入回收站", ["action.language"] = "切换语言",
        ["risk.safe"] = "安全可删", ["risk.caution"] = "谨慎可删", ["risk.blocked"] = "禁止直删",
        ["reason.cache"] = "可重新生成的应用或浏览器缓存", ["reason.temp"] = "临时文件，可由系统或应用重新生成",
        ["reason.thumbnail"] = "Windows 可重新生成的缩略图缓存", ["reason.crash"] = "崩溃报告或转储文件",
        ["reason.update"] = "Windows 更新下载残留，建议确认后处理", ["reason.old_download"] = "下载目录中超过 90 天未修改的文件，请确认是否仍需要",
        ["reason.personal"] = "个人文件，必须由用户确认",
        ["reason.large"] = "大文件，删除前确认是否仍需要", ["reason.system"] = "系统或软件核心文件，只展示占用",
        ["reason.other"] = "普通文件，默认不提供删除", ["media.unchecked"] = "未检查", ["media.ok"] = "正常",
        ["media.suspect"] = "疑似损坏", ["similar.none"] = "未发现明显相似的照片", ["similar.summary"] = "发现 {0} 组可能相似的照片，请逐项确认；最多可释放 {1}", ["detail.none"] = "选择一行可查看完整路径、风险和原因",
        ["cleanup.selected"] = "已选择：{0}，预计释放 {1}", ["cleanup.empty"] = "清理清单为空", ["cleanup.deleting"] = "正在删除…",
        ["cleanup.select_row"] = "请先选择要从清单移除的项目", ["cleanup.removed"] = "已从清理清单移除 {0} 个项目",
        ["cleanup.select_items"] = "请先勾选要处理的项目",
        ["cleanup.add_result"] = "已加入 {0} 个；已在清单 {1} 个；受保护 {2} 个；已不存在 {3} 个",
        ["duplicate.summary"] = "发现 {0} 组精确重复，最多可释放 {1}", ["duplicate.none"] = "未发现精确重复文件", ["duplicate.select_first"] = "请先勾选要加入清理清单的重复副本。保留副本不会自动选择。",
        ["duplicate.select_row"] = "请先选择一个重复文件",
        ["status.cancelled_analysis"] = "分析已取消", ["analysis.failed"] = "分析未完成；文件可能已移动或磁盘已断开",
        ["shell.missing"] = "文件已被移动或删除", ["shell.open_failed"] = "Windows 无法打开这个文件；可能没有关联程序或权限不足",
        ["apps.uninstall_started"] = "已打开官方卸载程序。卸载完成后，请点击“检查卸载残留”。",
        ["residual.title"] = "卸载后留下的内容", ["residual.hint"] = "只显示与刚卸载软件的安装路径或名称完全匹配的项目。注册表项目仅供查看，CleanSpace 不会自动删除。",
        ["apps.confirm_uninstall"] = "启动“{0}”的官方卸载程序？",
        ["residual.uninstall_first"] = "请先从此页面启动一个软件的官方卸载程序",
        ["residual.still_installed"] = "这个软件仍在已安装列表中。请完成卸载后再检查。",
        ["residual.none"] = "没有找到能够可靠关联的卸载残留", ["residual.found"] = "找到 {0} 个能够关联的残留项目",
        ["residual.scan_failed"] = "无法完成残留检查", ["residual.select_folders"] = "请先选择要移入回收站的残留文件夹",
        ["residual.changed"] = "残留位置已经变化，请重新检查", ["residual.recycle_failed"] = "部分残留未能移入回收站",
        ["residual.confirm_recycle"] = "将 {1} 卸载后留下的 {0} 个文件夹移入 Windows 回收站？",
        ["residual.recycled"] = "已移入回收站：{0} 个；未处理：{1} 个",
        ["residual.install_location"] = "卸载记录中的安装位置", ["residual.app_data"] = "名称完全匹配的应用数据",
        ["residual.publisher_data"] = "发布者目录中名称完全匹配的数据", ["residual.registry"] = "名称完全匹配的注册表项",
        ["cleanup.operation_failed"] = "清理未完成，未成功处理的文件已保留",
        ["residual.risk_confirm"] = "需要确认，只能移入回收站", ["residual.risk_registry"] = "仅显示，不自动删除",
        ["shell.locate_failed"] = "无法在资源管理器中定位这个文件",
        ["apps.open_settings_failed"] = "无法打开 Windows 应用设置", ["apps.uninstall_failed"] = "无法启动这个软件的卸载程序",
        ["apps.loaded"] = "已读取 {0} 个已安装软件", ["apps.load_failed"] = "无法读取已安装软件列表",
        ["apps.select_first"] = "请先选择要管理的软件",
        ["language.title"] = "请选择语言 · 언어를 선택하세요", ["language.hint"] = "每次启动都可以选择 · 실행할 때마다 선택할 수 있습니다",
        ["confirm.title"] = "请再次确认", ["confirm.recycle"] = "将 {0} 个项目移入 Windows 回收站？\n预计释放：{1}", ["cleanup.permanent"] = "不移入回收站",
        ["confirm.permanent"] = "将直接删除 {0} 个项目，不移入 Windows 回收站。\n预计释放：{1}\n请确认是否继续。", ["cleanup.deleted"] = "已删除（未移入回收站）", ["cleanup.delete_failed"] = "删除失败，文件已保留",
        ["locked.title"] = "文件正在使用", ["locked.heading"] = "部分文件被其他程序占用",
        ["locked.explanation"] = "其他文件已继续处理。下面会一次列出全部被占用的文件和程序；请选择一次批量关闭、重试，或暂时跳过。",
        ["locked.process"] = "占用程序", ["locked.unknown_process"] = "未识别到占用程序", ["locked.error"] = "错误代码", ["locked.retry"] = "重试",
        ["locked.close_retry"] = "关闭占用程序，清理后重新打开", ["locked.force_retry"] = "强制结束占用程序，清理后重新打开", ["locked.schedule"] = "下次重启时自动清理", ["locked.skip"] = "暂时跳过",
        ["locked.close_disabled"] = "包含 Windows 核心进程，禁止自动关闭。", ["locked.force_tip"] = "可能丢失占用程序中未保存的数据。",
        ["locked.force_confirm"] = "将强制结束以下占用程序，然后再次移入回收站：\n\n{0}\n\n这可能导致这些程序中未保存的数据丢失。是否继续？",
        ["locked.force_failed"] = "无法安全结束一个或多个占用程序。文件已保留。",
        ["locked.restart_failed"] = "文件处理已完成，但 Windows 未能自动重新打开部分程序，请手动启动。",
        ["locked.processing"] = "正在批量关闭占用程序并重试，请稍候…",
        ["locked.schedule_confirm"] = "这些文件当前无法处理。是否安排在 Windows 下次重启时删除？\n\n删除时不会移入回收站。",
        ["cleanup.scheduled"] = "已安排在下次重启时删除", ["locked.schedule_failed"] = "无法安排下次重启清理，文件已保留",
        ["cleanup.recycled"] = "已移入回收站", ["cleanup.changed"] = "扫描后文件已变化，已跳过", ["cleanup.locked"] = "文件被占用，已跳过", ["cleanup.progress"] = "正在清理：{0}/{1} 个项目",
        ["warning.blocked"] = "禁止直删项目不会被选择或删除。", ["about.text"] = "CleanSpace（허준영 制作）\n\n所有操作都在本机完成，不上传文件、路径或扫描数据。\n删除时可以选择是否使用 Windows 回收站。",
        ["settings.text"] = "语言可以即时切换。正式版使用 C# / .NET 10 WPF。",
        ["dashboard.empty"] = "选择“仅扫描系统盘”或“扫描所有本地磁盘”开始。扫描过程中会实时显示结果。",
        ["dashboard.summary"] = "已扫描 {0} 个文件，共 {1}\n安全缓存候选：{2}\n1 GB 以上大文件：{3}",
        ["filter.placeholder"] = "按文件名或完整路径筛选", ["scan.elapsed"] = "用时 {0:0.0} 秒"
    };

    private static readonly IReadOnlyDictionary<string, string> Korean = new Dictionary<string, string>
    {
        ["app.title"] = "CleanSpace (허준영 제작)",
        ["nav.dashboard"] = "디스크 개요", ["nav.space"] = "공간 분석", ["nav.media"] = "사진 및 동영상",
        ["nav.duplicates"] = "중복 파일", ["nav.apps"] = "프로그램 관리", ["nav.cleanup"] = "정리 목록",
        ["nav.history"] = "기록", ["nav.settings"] = "설정", ["nav.about"] = "정보",
        ["scan.system"] = "시스템 드라이브만 검사", ["scan.all"] = "연결된 드라이브 모두 검사", ["action.refresh_drives"] = "드라이브 새로 고침",
        ["scan.choice"] = "빠르게 확인하려면 시스템 드라이브만 검사하세요. 모든 드라이브 검사는 현재 연결된 내장 드라이브와 외장 하드, USB까지 확인합니다.",
        ["admin.banner_title"] = "일반 모드로도 사용할 수 있습니다",
        ["admin.banner_text"] = "지금은 관리자 권한 없이 실행 중입니다. 사용자 파일과 대부분의 캐시는 검사할 수 있지만 일부 Windows 폴더는 열지 못할 수 있습니다. 시스템 영역까지 확인하려면 관리자 권한으로 다시 실행하세요.",
        ["admin.continue"] = "일반 모드로 계속", ["admin.restart"] = "관리자 권한으로 다시 실행",
        ["admin.cancelled"] = "관리자 실행을 취소했습니다. 일반 모드로 계속합니다.", ["admin.failed"] = "관리자 권한으로 다시 실행하지 못했습니다.",
        ["admin.standard_active"] = "일반 모드로 실행 중입니다. 보호된 폴더는 검사하지 못할 수 있습니다.",
        ["drive.refreshed"] = "검사할 수 있는 드라이브 {0}개를 찾았습니다.", ["drive.none"] = "검사할 수 있는 드라이브가 없습니다.",
        ["drive.system"] = "Windows 시스템 드라이브", ["drive.fixed"] = "내장 드라이브", ["drive.removable"] = "외장 드라이브 또는 USB",
        ["action.remove_cleanup"] = "목록에서 빼기",
        ["action.select_file"] = "먼저 파일을 선택하세요.", ["action.scan_first"] = "먼저 드라이브 검사를 마쳐 주세요.", ["media.scan_first"] = "드라이브를 검사한 뒤 사진과 동영상을 확인할 수 있습니다.",
        ["scan.pause"] = "일시 중지", ["scan.resume"] = "계속", ["scan.cancel"] = "취소",
        ["action.admin"] = "관리자 권한으로 실행", ["action.admin_active"] = "관리자 권한으로 실행 중",
        ["status.ready"] = "준비됐습니다", ["status.scanning"] = "검사 중: {0}", ["status.paused"] = "검사를 일시 중지했습니다. 지금까지 찾은 결과는 계속 확인할 수 있습니다.",
        ["status.done"] = "검사 완료: 파일 {0}개, 총 {1}", ["status.cancelled"] = "검사를 취소했습니다. 지금까지 찾은 {0}개 항목은 그대로 표시합니다.",
        ["status.errors"] = "열지 못한 위치: {0}개",
        ["status.busy"] = "다른 작업이 진행 중입니다. 잠시 기다려 주세요.", ["status.cancelling"] = "취소하고 있습니다…",
        ["filter.failed"] = "검색 결과를 불러오지 못했습니다.", ["scan.results_failed"] = "검사는 끝났지만 안전하게 정리할 항목을 불러오지 못했습니다.",
        ["title.dashboard"] = "디스크 개요", ["title.space"] = "공간 분석", ["title.media"] = "사진 및 동영상",
        ["title.duplicates"] = "중복 파일", ["title.apps"] = "프로그램 관리", ["title.cleanup"] = "정리 목록",
        ["title.history"] = "기록", ["title.settings"] = "설정", ["title.about"] = "CleanSpace 정보",
        ["col.select"] = "선택", ["col.name"] = "이름", ["col.path"] = "전체 경로", ["col.drive"] = "드라이브",
        ["col.size"] = "크기", ["col.modified"] = "수정 날짜", ["col.risk"] = "위험도", ["col.reason"] = "이유",
        ["col.status"] = "상태", ["col.publisher"] = "개발사", ["col.version"] = "버전", ["col.location"] = "위치", ["col.source"] = "확인 근거",
        ["action.locate"] = "파일 위치 열기", ["action.open"] = "파일 열기", ["action.add"] = "정리 목록에 추가", ["action.select_duplicates"] = "중복 파일 선택",
        ["action.check_media"] = "미디어 검사", ["action.find_duplicates"] = "중복 파일 검사", ["action.find_similar_photos"] = "비슷한 사진 찾기", ["action.refresh"] = "새로 고침",
        ["action.check_residuals"] = "제거 후 남은 항목 확인", ["action.recycle_residuals"] = "선택한 폴더를 휴지통으로 이동",
        ["action.uninstall"] = "제거 프로그램 실행", ["action.select_safe"] = "안전한 항목 모두 선택", ["action.select_allowed"] = "삭제 가능한 항목 모두 선택",
        ["action.clear_selection"] = "모두 선택 해제", ["action.recycle"] = "휴지통으로 이동", ["action.language"] = "언어 전환",
        ["risk.safe"] = "삭제해도 안전함", ["risk.caution"] = "확인 후 삭제", ["risk.blocked"] = "삭제할 수 없음",
        ["reason.cache"] = "앱이나 브라우저가 다시 만들 수 있는 캐시", ["reason.temp"] = "시스템이나 앱이 다시 만들 수 있는 임시 파일",
        ["reason.thumbnail"] = "Windows가 다시 만드는 미리 보기 캐시", ["reason.crash"] = "오류 보고서 또는 메모리 덤프",
        ["reason.update"] = "Windows 업데이트가 남긴 다운로드 파일입니다. 내용을 확인한 뒤 처리하세요.", ["reason.old_download"] = "다운로드 폴더에서 90일 넘게 바뀌지 않은 파일입니다. 아직 필요한지 확인하세요.",
        ["reason.personal"] = "개인 파일이므로 직접 확인해야 합니다",
        ["reason.large"] = "용량이 큰 파일입니다. 아직 필요한지 확인하세요.", ["reason.system"] = "시스템이나 프로그램에 필요한 핵심 파일입니다. 용량만 표시합니다.",
        ["reason.other"] = "일반 파일입니다. 기본적으로 삭제할 수 없습니다.", ["media.unchecked"] = "검사 안 함", ["media.ok"] = "정상",
        ["media.suspect"] = "손상 의심", ["similar.none"] = "비슷해 보이는 사진이 없습니다.", ["similar.summary"] = "비슷해 보이는 사진 {0}묶음을 찾았습니다. 직접 확인한 뒤 정리하세요. 최대 {1}를 확보할 수 있습니다.", ["detail.none"] = "항목을 선택하면 전체 경로와 위험도, 분류 이유를 볼 수 있습니다.",
        ["cleanup.selected"] = "선택: {0}개, 확보 예정: {1}", ["cleanup.empty"] = "정리 목록이 비어 있습니다", ["cleanup.deleting"] = "삭제 중…",
        ["cleanup.select_row"] = "목록에서 뺄 항목을 선택하세요.", ["cleanup.removed"] = "정리 목록에서 {0}개를 뺐습니다.",
        ["cleanup.select_items"] = "처리할 항목을 먼저 체크하세요.",
        ["cleanup.add_result"] = "{0}개를 추가했습니다. 이미 목록에 있던 항목 {1}개, 보호된 항목 {2}개, 사라진 파일 {3}개는 건너뛰었습니다.",
        ["duplicate.summary"] = "같은 파일을 {0}묶음 찾았습니다. 최대 {1}까지 확보할 수 있습니다.", ["duplicate.none"] = "같은 파일이 없습니다.", ["duplicate.select_first"] = "정리 목록에 넣을 중복 파일을 먼저 체크하세요. 남겨 둘 파일은 자동으로 선택하지 않습니다.",
        ["duplicate.select_row"] = "먼저 중복 파일을 선택하세요.",
        ["status.cancelled_analysis"] = "분석을 취소했습니다.", ["analysis.failed"] = "분석을 마치지 못했습니다. 파일이 옮겨졌거나 드라이브 연결이 끊겼을 수 있습니다.",
        ["shell.missing"] = "파일이 이동되었거나 삭제되었습니다.", ["shell.open_failed"] = "이 파일을 열 수 없습니다. 연결된 프로그램이나 권한을 확인하세요.",
        ["shell.locate_failed"] = "파일 위치를 탐색기에서 열지 못했습니다.",
        ["apps.open_settings_failed"] = "Windows 앱 설정을 열지 못했습니다.", ["apps.uninstall_failed"] = "이 프로그램의 제거 도구를 실행하지 못했습니다.",
        ["apps.uninstall_started"] = "제거 프로그램을 열었습니다. 제거가 끝나면 '남은 항목 확인'을 누르세요.",
        ["residual.title"] = "제거 후 남은 항목", ["residual.hint"] = "설치 경로나 이름이 정확히 일치하는 항목만 표시합니다. 레지스트리 항목은 확인용이며 CleanSpace가 삭제하지 않습니다.",
        ["residual.uninstall_first"] = "먼저 이 화면에서 프로그램 제거를 시작하세요.",
        ["apps.confirm_uninstall"] = "'{0}' 제거 프로그램을 실행할까요?",
        ["residual.still_installed"] = "아직 설치된 프로그램으로 확인됩니다. 제거를 마친 뒤 다시 확인하세요.",
        ["residual.none"] = "이 프로그램과 확실히 연결되는 항목이 없습니다.", ["residual.found"] = "이 프로그램과 연결되는 항목 {0}개를 찾았습니다.",
        ["residual.scan_failed"] = "남은 항목을 확인하지 못했습니다.", ["residual.select_folders"] = "휴지통으로 옮길 폴더를 먼저 선택하세요.",
        ["residual.changed"] = "폴더 위치가 바뀌었습니다. 다시 확인해 주세요.", ["residual.recycle_failed"] = "일부 폴더를 휴지통으로 옮기지 못했습니다.",
        ["residual.confirm_recycle"] = "{1} 제거 후 남은 폴더 {0}개를 Windows 휴지통으로 옮길까요?",
        ["residual.recycled"] = "{0}개를 휴지통으로 옮겼습니다. 옮기지 못한 폴더는 {1}개입니다.",
        ["residual.install_location"] = "제거 정보에 기록된 설치 경로", ["residual.app_data"] = "이름이 정확히 일치하는 앱 데이터",
        ["cleanup.operation_failed"] = "정리를 마치지 못했습니다. 처리되지 않은 파일은 그대로 두었습니다.",
        ["residual.publisher_data"] = "개발사 폴더에서 이름이 일치하는 데이터", ["residual.registry"] = "이름이 정확히 일치하는 레지스트리 항목",
        ["residual.risk_confirm"] = "확인 후 휴지통으로 이동 가능", ["residual.risk_registry"] = "확인만 가능하며 자동 삭제 안 함",
        ["apps.loaded"] = "설치된 프로그램 {0}개를 불러왔습니다.", ["apps.load_failed"] = "설치된 프로그램 목록을 불러오지 못했습니다.",
        ["apps.select_first"] = "관리할 프로그램을 먼저 선택하세요.",
        ["language.title"] = "请选择语言 · 언어를 선택하세요", ["language.hint"] = "每次启动都可以选择 · 실행할 때마다 선택할 수 있습니다",
        ["confirm.title"] = "한 번 더 확인해 주세요", ["confirm.recycle"] = "{0}개 항목을 Windows 휴지통으로 옮길까요?\n확보 예정 공간: {1}", ["cleanup.permanent"] = "휴지통 없이 삭제",
        ["confirm.permanent"] = "{0}개 항목을 Windows 휴지통으로 보내지 않고 바로 삭제합니다.\n확보 예정 공간: {1}\n계속할까요?", ["cleanup.deleted"] = "휴지통을 거치지 않고 삭제함", ["cleanup.delete_failed"] = "삭제하지 못해 파일을 그대로 두었습니다.",
        ["locked.title"] = "파일 사용 중", ["locked.heading"] = "일부 파일을 다른 프로그램에서 사용 중입니다",
        ["locked.explanation"] = "다른 파일은 계속 처리되었습니다. 아래에 사용 중인 모든 파일과 프로그램을 한 번에 표시합니다. 일괄 종료 후 재시도하거나 건너뛸 수 있습니다.",
        ["locked.process"] = "사용 중인 프로그램", ["locked.unknown_process"] = "사용 중인 프로그램을 확인하지 못함", ["locked.error"] = "오류 코드", ["locked.retry"] = "다시 시도",
        ["locked.close_retry"] = "사용 중인 앱을 닫고 정리한 뒤 다시 열기", ["locked.force_retry"] = "사용 중인 앱을 강제로 끝내고 다시 열기", ["locked.schedule"] = "다시 시작할 때 정리", ["locked.skip"] = "이번에는 건너뛰기",
        ["locked.close_disabled"] = "Windows 핵심 프로세스가 포함되어 자동으로 닫을 수 없습니다.", ["locked.force_tip"] = "해당 프로그램에서 저장하지 않은 데이터가 사라질 수 있습니다.",
        ["locked.force_confirm"] = "다음 프로그램을 강제로 종료한 뒤 휴지통 이동을 다시 시도합니다:\n\n{0}\n\n저장하지 않은 데이터가 손실될 수 있습니다. 계속할까요?",
        ["locked.force_failed"] = "하나 이상의 사용 중인 프로그램을 안전하게 종료하지 못했습니다. 파일을 보존했습니다.",
        ["locked.restart_failed"] = "파일 처리는 완료되었지만 Windows가 일부 프로그램을 자동으로 다시 열지 못했습니다. 직접 실행하세요.",
        ["locked.processing"] = "파일을 사용 중인 프로그램을 닫고 다시 시도하고 있습니다. 잠시 기다려 주세요…",
        ["locked.schedule_confirm"] = "지금 처리할 수 없는 파일입니다. Windows를 다시 시작할 때 휴지통을 거치지 않고 삭제할까요?\n\n다시 시작한 뒤에는 휴지통에서 되돌릴 수 없습니다.",
        ["cleanup.scheduled"] = "다음 재시작 때 삭제 예약됨", ["locked.schedule_failed"] = "다음 재시작 예약 실패, 파일을 보존했습니다",
        ["cleanup.recycled"] = "휴지통으로 이동", ["cleanup.changed"] = "검사 후 파일이 바뀌어 건너뜀", ["cleanup.locked"] = "다른 프로그램에서 사용 중이라 건너뜀", ["cleanup.progress"] = "정리 중: {0}/{1}개 항목",
        ["warning.blocked"] = "보호된 항목은 선택하거나 삭제할 수 없습니다.", ["about.text"] = "CleanSpace (허준영 제작)\n\n모든 작업은 이 PC에서 처리하며 파일, 경로, 검사 결과를 서버로 보내지 않습니다.\n삭제할 때는 Windows 휴지통을 사용할지 직접 선택할 수 있습니다.",
        ["settings.text"] = "언어는 바로 바꿀 수 있습니다. CleanSpace는 C#과 .NET 10 WPF로 만들었습니다.",
        ["dashboard.empty"] = "위에서 '시스템 드라이브만 검사' 또는 '연결된 드라이브 모두 검사'를 선택하세요. 검사 결과는 바로 표시됩니다.",
        ["dashboard.summary"] = "파일 {0}개, 총 {1}\n안전하게 정리할 수 있는 캐시: {2}\n1 GB 이상인 파일: {3}",
        ["filter.placeholder"] = "파일 이름이나 전체 경로 검색", ["scan.elapsed"] = "걸린 시간: {0:0.0}초"
    };
}
