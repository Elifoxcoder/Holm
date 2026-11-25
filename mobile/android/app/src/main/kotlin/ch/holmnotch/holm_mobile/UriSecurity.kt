package ch.holmnotch.holm_mobile

import android.content.Context
import android.net.Uri
import java.util.Locale

/**
 * The only content URIs Holm accepts from Android's share sheet or the notch. In particular, a caller must not be
 * able to make Holm read one of its own private files through a provider it controls.
 */
internal object UriSecurity {
    fun isAllowedContentUri(context: Context, uri: Uri): Boolean {
        if (!uri.scheme.equals("content", ignoreCase = true) || uri.isOpaque) return false
        // Content authorities are host[:port], without URI user-info.
        if (uri.authority.isNullOrBlank() || uri.userInfo != null || uri.port != -1) return false

        val ownPackage = context.packageName.lowercase(Locale.ROOT)
        val authority = uri.authority!!.lowercase(Locale.ROOT)
        if (authority == ownPackage || authority.startsWith("$ownPackage.")) return false

        // Check the provider's owning package as well as its spelling: Holm may expose more than one authority.
        val owner = runCatching {
            context.packageManager.resolveContentProvider(uri.authority!!, 0)?.packageName
        }.getOrNull()
        if (owner == null || owner.equals(context.packageName, ignoreCase = true)) return false

        // A third-party provider must not be handed a path into Holm's private data either.
        val path = uri.path.orEmpty().lowercase(Locale.ROOT)
        val packagePattern = Regex.escape(ownPackage)
        if (Regex("""/data/data/$packagePattern(?:/|$)""").containsMatchIn(path)) return false
        if (Regex("""/data/user(?:_de)?/\d+/$packagePattern(?:/|$)""").containsMatchIn(path)) return false
        if (Regex("""/android/(?:data|obb)/$packagePattern(?:/|$)""").containsMatchIn(path)) return false
        return true
    }
}
