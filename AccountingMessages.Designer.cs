﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace Grammophone.Domos.Accounting {
    using System;
    
    
    /// <summary>
    ///   A strongly-typed resource class, for looking up localized strings, etc.
    /// </summary>
    // This class was auto-generated by the StronglyTypedResourceBuilder
    // class via a tool like ResGen or Visual Studio.
    // To add or remove a member, edit your .ResX file then rerun ResGen
    // with the /str option, or rebuild your VS project.
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "15.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    internal class AccountingMessages {
        
        private static global::System.Resources.ResourceManager resourceMan;
        
        private static global::System.Globalization.CultureInfo resourceCulture;
        
        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal AccountingMessages() {
        }
        
        /// <summary>
        ///   Returns the cached ResourceManager instance used by this class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Resources.ResourceManager ResourceManager {
            get {
                if (object.ReferenceEquals(resourceMan, null)) {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Grammophone.Domos.Accounting.AccountingMessages", typeof(AccountingMessages).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }
        
        /// <summary>
        ///   Overrides the current thread's CurrentUICulture property for all
        ///   resource lookups using this strongly typed resource class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Globalization.CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Deplete the outgoing transfer account upon remittance success..
        /// </summary>
        internal static string DEPLETE_TRANSFER_ACCOUNT {
            get {
                return ResourceManager.GetString("DEPLETE_TRANSFER_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Fund the main account upong transfer success..
        /// </summary>
        internal static string FUND_MAIN_ACCOUNT {
            get {
                return ResourceManager.GetString("FUND_MAIN_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Actions for funds transfer event of type {0}..
        /// </summary>
        internal static string GENERIC_FUNDS_TRANSFER_JOURNAL {
            get {
                return ResourceManager.GetString("GENERIC_FUNDS_TRANSFER_JOURNAL", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to There is at least one account which cannot be charged because of insufficient balance..
        /// </summary>
        internal static string INSUFFICIENT_BALANCE {
            get {
                return ResourceManager.GetString("INSUFFICIENT_BALANCE", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to The journal has already been executed and persisted..
        /// </summary>
        internal static string JOURNAL_ALREADY_EXECUTED {
            get {
                return ResourceManager.GetString("JOURNAL_ALREADY_EXECUTED", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to The line in the journal has already been executed..
        /// </summary>
        internal static string JOURNAL_LINE_ALREADY_EXECUTED {
            get {
                return ResourceManager.GetString("JOURNAL_LINE_ALREADY_EXECUTED", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Move withdrawn amount from main account..
        /// </summary>
        internal static string MOVE_AMOUNT_FROM_MAIN_ACCOUNT {
            get {
                return ResourceManager.GetString("MOVE_AMOUNT_FROM_MAIN_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Move refunded amount from outgoing transfer account..
        /// </summary>
        internal static string MOVE_AMOUNT_FROM_TRANSFER_ACCOUNT {
            get {
                return ResourceManager.GetString("MOVE_AMOUNT_FROM_TRANSFER_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Move refunded amount to main account..
        /// </summary>
        internal static string MOVE_AMOUNT_TO_MAIN_ACCOUNT {
            get {
                return ResourceManager.GetString("MOVE_AMOUNT_TO_MAIN_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Move withdrawn amount to outgoung transfer account..
        /// </summary>
        internal static string MOVE_AMOUNT_TO_TRANSFER_ACCOUNT {
            get {
                return ResourceManager.GetString("MOVE_AMOUNT_TO_TRANSFER_ACCOUNT", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Refund failed transfer..
        /// </summary>
        internal static string REFUND_FAILED_TRANSFER {
            get {
                return ResourceManager.GetString("REFUND_FAILED_TRANSFER", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to The funds transfer has succeeded..
        /// </summary>
        internal static string TRANSFER_SUCCEEDED {
            get {
                return ResourceManager.GetString("TRANSFER_SUCCEEDED", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to The postings amounts within the journal don&apos;t sum to zero..
        /// </summary>
        internal static string UNBALANCED_POSTINGS {
            get {
                return ResourceManager.GetString("UNBALANCED_POSTINGS", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Move withdrawed funds to outgoing transfer account..
        /// </summary>
        internal static string WITHDRAWAL_TRANSFER_DESCRIPTION {
            get {
                return ResourceManager.GetString("WITHDRAWAL_TRANSFER_DESCRIPTION", resourceCulture);
            }
        }
    }
}
